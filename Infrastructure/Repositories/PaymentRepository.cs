using Domain.Entities;
using Application.DTO;
using Application.Interfaces;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Domain.ValueObjects;

namespace Infrastructure.Repositories
{
    public class PaymentRepository : IPayment
    {
        private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;

        public PaymentRepository(IDbContextFactory<ApplicationDbContext> contextFactory)
        {
            _contextFactory = contextFactory;
        }

        public async Task CreatePaymentAsync(CreatePaymentDTO paymentDTO)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            using var transaction = await context.Database.BeginTransactionAsync();

            try
            {
                // 1. Fetch Disbursement
                var disbursement = await context.Disbursements
                    .Include(d => d.LoanApplication)
                    .Include(d => d.Payments)
                    .FirstOrDefaultAsync(d => d.Id == paymentDTO.DisbursementId);

                if (disbursement == null) throw new Exception("Disbursement not found.");

                // 2. CHECK: Prevent payment if today is before the Loan Start Date
                if (DateTime.Now.Date < disbursement.StartDate.Date)
                {
                    throw new InvalidOperationException($"The installment cycle for this loan starts on {disbursement.StartDate:MMMM dd, yyyy}. Please wait for the cycle to begin.");
                }

                // 3. CHECK: Prevent payment if current installment is already settled
                var (remainingPeriodAmount, nextDueDate) = await GetNextScheduledPaymentAsync(paymentDTO.DisbursementId);
                
                if (remainingPeriodAmount <= 0)
                {
                    throw new InvalidOperationException($"The current installment is already fully paid. The next payment is due after {nextDueDate:MMMM dd, yyyy}.");
                }

                // 4. Calculate Total Remaining Debt for the whole loan
                var totalPaidSoFar = disbursement.Payments
                    .Where(p => p.IsActive)
                    .Sum(p => p.Amount);

                decimal remainingTotalDebt = disbursement.Amount - totalPaidSoFar;

                // 5. Validate Final Installment Overpayment
                bool isLastInstallment = remainingTotalDebt <= remainingPeriodAmount;

                if (isLastInstallment && paymentDTO.Amount > remainingTotalDebt)
                {
                    throw new InvalidOperationException($"This is the final payment. Please pay exactly {remainingTotalDebt:N2} to close the loan.");
                }

                // 6. Update Account Balance
                var account = await context.Accounts.FirstOrDefaultAsync(a => a.Id == paymentDTO.AccountId);
                if (account == null) throw new Exception("Target account not found.");
                
                account.Balance += paymentDTO.Amount;

                // 7. Create Payment Record
                var payment = new Payment
                {
                    DisbursementId = paymentDTO.DisbursementId,
                    AccountId = paymentDTO.AccountId,
                    PaymentTypeId = paymentDTO.PaymentTypeId,
                    Amount = paymentDTO.Amount,
                    PaymentDate = DateTime.Now,
                    IsActive = true,
                    CreatedAt = DateTime.Now
                };

                context.Payments.Add(payment);

                // 8. Update Loan Status if fully paid
                if (totalPaidSoFar + paymentDTO.Amount >= disbursement.Amount)
                {
                    disbursement.LoanApplication.Status = LoanStatus.Paid;
                }

                await context.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task CreatePaymentWithPenaltyAsync(CreatePaymentDTO paymentDTO, decimal shortfall, decimal penaltyAmount)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            using var transaction = await context.Database.BeginTransactionAsync();

            try
            {
                // 1. Fetch Disbursement + Application + LoanProduct + Setting
                var disbursement = await context.Disbursements
                    .Include(d => d.LoanApplication)
                        .ThenInclude(la => la.LoanProductSetting) // Updated navigation property name
                    
                    .Include(d => d.Payments)
                    .FirstOrDefaultAsync(d => d.Id == paymentDTO.DisbursementId);

                if (disbursement == null) throw new Exception("Disbursement not found.");

                // 2. Record Payment Amount in the target Account
                var account = await context.Accounts.FirstOrDefaultAsync(a => a.Id == paymentDTO.AccountId);
                if (account == null) throw new Exception("Target account not found.");
                account.Balance += paymentDTO.Amount;

                // 3. Get actual penalty rate from Product Settings for the description
                decimal rate = disbursement.LoanApplication?.LoanProductSetting?.PenalityRate ?? 0;

                // 4. Create Penalty Record (Populates the Penalty Page)
                var penalty = new Penality
                {
                    LoanApplicationId = disbursement.LoanApplicationId,
                    Amount = penaltyAmount,
                    Date = DateTime.Now,
                    ReasonId = 1, 
                    Description = $"Penalty ({rate}%). Shortfall: {shortfall:N2}.",
                    IsActive = true
                };
                context.Penalties.Add(penalty);

                // 5. Add Penalty to the Loan Balance (Reflected in Loan Details)
                // We add ONLY the penaltyAmount. The shortfall is already part of the principal.
                disbursement.Amount += penaltyAmount;

                // 6. Create the Payment Record
                var payment = new Payment
                {
                    DisbursementId = paymentDTO.DisbursementId,
                    AccountId = paymentDTO.AccountId,
                    PaymentTypeId = paymentDTO.PaymentTypeId,
                    Amount = paymentDTO.Amount,
                    PaymentDate = DateTime.Now,
                    IsActive = true,
                    CreatedAt = DateTime.Now
                };
                context.Payments.Add(payment);

                // 7. Check if loan is finished
                decimal totalPaidSoFar = disbursement.Payments
                    .Where(p => p.IsActive)
                    .Sum(p => (decimal?)p.Amount ?? 0) + paymentDTO.Amount;

                if (totalPaidSoFar >= disbursement.Amount)
                {
                    disbursement.LoanApplication.Status = LoanStatus.Paid;
                }

                await context.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<(decimal Amount, DateTime Date)> GetNextScheduledPaymentAsync(int disbursementId)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            
            var disbursement = await context.Disbursements
                .Include(d => d.PaymentModality)
                .FirstOrDefaultAsync(d => d.Id == disbursementId);

            if (disbursement == null) return (0, DateTime.Today);

            var totalPaid = await context.Payments
                .Where(p => p.DisbursementId == disbursementId && p.IsActive)
                .SumAsync(p => (decimal?)p.Amount ?? 0);

            decimal installmentAmount = disbursement.Amount / disbursement.TotalInstallments;
            string mode = disbursement.PaymentModality?.Mode?.ToLower() ?? "monthly";
            DateTime today = DateTime.Today;

            for (int i = 0; i < disbursement.TotalInstallments; i++)
            {
                DateTime dueDate = mode switch
                {
                    "daily" => disbursement.StartDate.AddDays(i),
                    "weekly" => disbursement.StartDate.AddDays(i * 7),
                    "monthly" => disbursement.StartDate.AddMonths(i),
                    "yearly" => disbursement.StartDate.AddYears(i),
                    _ => disbursement.StartDate.AddMonths(i)
                };

                decimal cumulativeDueSoFar = installmentAmount * (i + 1);

                if (today <= dueDate || totalPaid < cumulativeDueSoFar)
                {
                    decimal paidForPreviousPeriods = installmentAmount * i;
                    decimal paidTowardsCurrent = Math.Max(0, totalPaid - paidForPreviousPeriods);
                    decimal remaining = Math.Max(0, installmentAmount - paidTowardsCurrent);

                    return (remaining, dueDate);
                }
            }
            
            return (0, DateTime.Today);
        }

        public async Task<List<Payment>> GetAllPaymentsAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.Payments
                .Include(i => i.Disbursement).ThenInclude(d => d.LoanApplication).ThenInclude(l => l.Borrower)
                .Include(i => i.Account)
                .Include(i => i.PaymentType)
                .OrderByDescending(p => p.PaymentDate)
                .ToListAsync();
        }

        public async Task<Payment?> GetPaymentByIdAsync(int id)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.Payments
                .Include(i => i.Disbursement)
                .Include(i => i.Account)
                .Include(i => i.PaymentType)
                .FirstOrDefaultAsync(i => i.Id == id);
        }
    }
}