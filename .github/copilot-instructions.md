# Digital Loan Platform — Copilot Base Instructions

## Project

Multi-tenant SaaS for BNR-authorized Tier 2/3 lenders in Rwanda. Handles the
full lending lifecycle from KYC to settlement.

## Stack

- .NET Core
- Entity Framework Core
- ASP.NET Identity Core
- Blazor Server + MudBlazor (Web layer)

## Architecture: Clean Architecture

- **Core/Domain**: entities, value objects, domain events, enums — zero external
  dependencies
- **Core/Application**: use cases, interfaces, DTOs, validators, CQRS handlers —
  no EF/HTTP references
- **Infrastructure**: EF Core DbContext, repositories, identity, external
  services
- **Web**: Blazor pages/components, MudBlazor UI, code-behind — calls
  Application layer only

## Branching

GitHub Flow — feature branches → PR → main. No direct commits to main.

## Key Domain Concepts

- Borrowers have a Global Identity (NIDA) with tenant-specific accounts per
  lender
- Interest clock starts ONLY after Confirm Disbursement — never at application
  or approval
- Grace period = maturity shift (loan end date shifts forward, no accrual during
  window)
- Payment waterfall (strict): Confirmed Penalties → Past Due Interest → Current
  Interest → Principal
- Penalties are Draft until Manager confirms — drafts never affect borrower
  balance
- Frozen Default: balance locked, only Recovery/Settlement entries allowed
- No deletion of financial records — reversals and adjustments only
- Maker (Loan Officer) creates, Checker (Manager) approves — self-approval
  allowed but must be audit-logged
