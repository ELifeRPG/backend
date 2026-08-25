using ELifeRPG.Banking.Api.BankAccounts;
using ELifeRPG.Banking.Api.Banks;
using ELifeRPG.Banking.Application.Companies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

public static class BankingModule
{
    public const string BankingManageScope = "gameserver:banking:manage";
    public const string BankingWriteScope = "gameserver:banking:write";
    private const string BankingManagePolicy = "Banking.Manage";
    private const string BankingWritePolicy = "Banking.Write";

    public static IServiceCollection AddBankingModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddBankingInfrastructure(configuration);

        services.AddAuthorizationBuilder()
            .AddPolicy(BankingManagePolicy, policy => policy.RequireAssertion(context => HasScope(context, BankingManageScope)))
            .AddPolicy(BankingWritePolicy, policy => policy.RequireAssertion(context => HasScope(context, BankingWriteScope)));

        return services;
    }

    public static WebApplication MapBankingModule(this WebApplication app)
    {
        var group = app.MapGroup("api").WithTags("Banking");

        group.MapPost("banks", async (
                [FromBody] OpenBankRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToCommand(), cancellationToken);
                return Results.Ok(BankDto.Create(result, request));
            })
            .RequireAuthorization(BankingManagePolicy)
            .Produces<BankDto>()
            .WithName("OpenBank")
            .WithDescription("Opens a new bank.");

        group.MapGet("banks", async (
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var banks = await mediator.Send(new BanksQuery(), cancellationToken);
                return Results.Ok(banks.Select(BankDto.Create).ToList());
            })
            .RequireAuthorization(BankingWritePolicy)
            .Produces<List<BankDto>>()
            .WithName("ListBanks")
            .WithDescription("Lists banks.");

        group.MapPost("banks/{bankId:guid}/accounts", async (
                Guid bankId,
                [FromBody] OpenBankAccountRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                if (request is { CharacterId: not null, CompanyId: not null } or { CharacterId: null, CompanyId: null })
                {
                    return Results.Problem(
                        title: "Exactly one of characterId or companyId must be provided",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                if (request.CharacterId is not null)
                {
                    var result = await mediator.Send(request.ToPersonalCommand(bankId), cancellationToken);

                    return result switch
                    {
                        OpenBankAccountResult.Opened opened => Results.Ok(BankAccountDto.Create(opened, bankId, request.CharacterId.Value)),
                        OpenBankAccountResult.BankNotFound => Results.Problem(title: "Bank not found", statusCode: StatusCodes.Status404NotFound),
                        OpenBankAccountResult.CharacterNotFound => Results.Problem(title: "Character not found", statusCode: StatusCodes.Status404NotFound),
                    };
                }

                var corporateResult = await mediator.Send(request.ToCorporateCommand(bankId), cancellationToken);

                return corporateResult switch
                {
                    OpenCorporateBankAccountResult.Opened opened => Results.Ok(BankAccountDto.Create(opened, bankId, request.CompanyId!.Value)),
                    OpenCorporateBankAccountResult.BankNotFound => Results.Problem(title: "Bank not found", statusCode: StatusCodes.Status404NotFound),
                    OpenCorporateBankAccountResult.CompanyNotFound => Results.Problem(title: "Company not found", statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(BankingWritePolicy)
            .Produces<BankAccountDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("OpenBankAccount")
            .WithDescription("Opens a bank account for a character (Personal) or a company (Corporate) — provide exactly one of characterId/companyId.");

        group.MapGet("characters/{characterId:guid}/bank-accounts", async (
                Guid characterId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var accounts = await mediator.Send(new BankAccountsByCharacterQuery(new CharacterId(characterId)), cancellationToken);
                return Results.Ok(accounts.Select(BankAccountDto.Create).ToList());
            })
            .RequireAuthorization(BankingWritePolicy)
            .Produces<List<BankAccountDto>>()
            .WithName("ListCharacterBankAccounts")
            .WithDescription("Lists a character's personal bank accounts.");

        group.MapGet("companies/{companyId:guid}/bank-accounts", async (
                Guid companyId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var accounts = await mediator.Send(new BankAccountsByCompanyQuery(new CompanyId(companyId)), cancellationToken);
                return Results.Ok(accounts.Select(BankAccountDto.Create).ToList());
            })
            .RequireAuthorization(BankingWritePolicy)
            .Produces<List<BankAccountDto>>()
            .WithName("ListCompanyBankAccounts")
            .WithDescription("Lists a company's corporate bank accounts.");

        group.MapGet("bank-accounts/{bankAccountId:guid}", async (
                Guid bankAccountId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new BankAccountDetailsQuery(new BankAccountId(bankAccountId)), cancellationToken);

                return result switch
                {
                    BankAccountDetailsResult.Found found => Results.Ok(BankAccountDto.Create(found.BankAccount)),
                    BankAccountDetailsResult.NotFound => Results.Problem(title: "Bank account not found", statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(BankingWritePolicy)
            .Produces<BankAccountDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetBankAccount")
            .WithDescription("Gets bank account details.");

        group.MapGet("bank-accounts/{bankAccountId:guid}/transactions", async (
                Guid bankAccountId,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(new BankAccountTransactionHistoryQuery(new BankAccountId(bankAccountId)), cancellationToken);

                return result switch
                {
                    BankAccountTransactionHistoryResult.Found found => Results.Ok(found.Transactions.Select(BankAccountTransactionDto.Create).ToList()),
                    BankAccountTransactionHistoryResult.BankAccountNotFound => Results.Problem(
                        title: "Bank account not found",
                        statusCode: StatusCodes.Status404NotFound),
                };
            })
            .RequireAuthorization(BankingWritePolicy)
            .Produces<List<BankAccountTransactionDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ListBankAccountTransactions")
            .WithDescription("Lists a bank account's most recent transactions (deposits, withdrawals, transfers), newest first.");

        group.MapPut("bank-accounts/{bankAccountId:guid}/deposit", async (
                Guid bankAccountId,
                [FromBody] DepositRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToCommand(bankAccountId), cancellationToken);

                return result switch
                {
                    DepositResult.Deposited deposited => Results.Ok(new TransactionResultDto
                    {
                        Amount = deposited.Amount,
                        Fee = deposited.Fee,
                        NewBalance = deposited.NewBalance,
                    }),
                    DepositResult.BankAccountNotFound => Results.Problem(title: "Bank account not found", statusCode: StatusCodes.Status404NotFound),
                    DepositResult.ConcurrentModification => Results.Problem(
                        title: "Another operation already committed against this account",
                        statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(BankingWritePolicy)
            .Produces<TransactionResultDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("DepositToBankAccount")
            .WithDescription("Deposits cash into a bank account.");

        group.MapPut("bank-accounts/{bankAccountId:guid}/withdraw", async (
                Guid bankAccountId,
                [FromBody] WithdrawRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToCommand(bankAccountId), cancellationToken);

                return result switch
                {
                    WithdrawResult.Withdrawn withdrawn => Results.Ok(new TransactionResultDto
                    {
                        Amount = withdrawn.Amount,
                        Fee = withdrawn.Fee,
                        NewBalance = withdrawn.NewBalance,
                    }),
                    WithdrawResult.BankAccountNotFound => Results.Problem(title: "Bank account not found", statusCode: StatusCodes.Status404NotFound),
                    WithdrawResult.NotAuthorized => Results.Problem(
                        title: "Not authorized to transact on this account",
                        statusCode: StatusCodes.Status403Forbidden),
                    WithdrawResult.InsufficientBalance => Results.Problem(
                        title: "Insufficient balance",
                        statusCode: StatusCodes.Status409Conflict),
                    WithdrawResult.ConcurrentModification => Results.Problem(
                        title: "Another operation already committed against this account",
                        statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(BankingWritePolicy)
            .Produces<TransactionResultDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("WithdrawFromBankAccount")
            .WithDescription("Withdraws cash from a bank account, e.g. for an ATM.");

        group.MapPut("bank-accounts/{bankAccountId:guid}/transaction", async (
                Guid bankAccountId,
                [FromBody] TransferRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToCommand(bankAccountId), cancellationToken);

                return result switch
                {
                    TransferResult.Transferred transferred => Results.Ok(new TransactionResultDto
                    {
                        Amount = transferred.Amount,
                        Fee = transferred.Fee,
                        NewBalance = transferred.NewBalance,
                    }),
                    TransferResult.BankAccountNotFound => Results.Problem(title: "Bank account not found", statusCode: StatusCodes.Status404NotFound),
                    TransferResult.TargetBankAccountNotFound => Results.Problem(
                        title: "Target bank account not found",
                        statusCode: StatusCodes.Status404NotFound),
                    TransferResult.NotAuthorized => Results.Problem(
                        title: "Not authorized to transact on this account",
                        statusCode: StatusCodes.Status403Forbidden),
                    TransferResult.InsufficientBalance => Results.Problem(
                        title: "Insufficient balance",
                        statusCode: StatusCodes.Status409Conflict),
                    TransferResult.ConcurrentModification => Results.Problem(
                        title: "Another operation already committed against this account",
                        statusCode: StatusCodes.Status409Conflict),
                };
            })
            .RequireAuthorization(BankingWritePolicy)
            .Produces<TransactionResultDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("TransferBankAccountFunds")
            .WithDescription("Transfers cash to another bank account.");

        group.MapPut("bank-accounts/{bankAccountId:guid}/purchase-company-shares", async (
                Guid bankAccountId,
                [FromBody] PurchaseCompanySharesRequestDto request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request.ToCommand(bankAccountId), cancellationToken);

                return result switch
                {
                    PurchaseCompanySharesResult.Purchased purchased => Results.Ok(new TransactionResultDto
                    {
                        Amount = purchased.TotalPaid,
                        Fee = purchased.Fee,
                        NewBalance = purchased.NewBalance,
                    }),
                    PurchaseCompanySharesResult.BankAccountNotFound => Results.Problem(title: "Bank account not found", statusCode: StatusCodes.Status404NotFound),
                    PurchaseCompanySharesResult.CompanyNotFound => Results.Problem(title: "Company not found", statusCode: StatusCodes.Status404NotFound),
                    PurchaseCompanySharesResult.NotAuthorized => Results.Problem(
                        title: "Not authorized to transact on this account",
                        statusCode: StatusCodes.Status403Forbidden),
                    PurchaseCompanySharesResult.InsufficientBalance => Results.Problem(
                        title: "Insufficient balance",
                        statusCode: StatusCodes.Status409Conflict),
                    PurchaseCompanySharesResult.InvalidQuantity => Results.Problem(
                        title: "Quantity must be positive",
                        statusCode: StatusCodes.Status400BadRequest),
                    PurchaseCompanySharesResult.InvalidPrice => Results.Problem(
                        title: "Price per share must be positive",
                        statusCode: StatusCodes.Status400BadRequest),
                };
            })
            .RequireAuthorization(BankingWritePolicy)
            .Produces<TransactionResultDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithName("PurchaseCompanyShares")
            .WithDescription("Debits a bank account and issues company shares to the acting character, atomically across the Banking and Companies modules.");

        return app;
    }

    private static bool HasScope(Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext context, string scope)
        => (context.User.FindFirst("scope")?.Value ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(scope);
}
