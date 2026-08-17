namespace ELifeRPG.Companies.Api.Companies;

public sealed record CompanyApplicationDto
{
    public required Guid ApplicationId { get; init; }

    public required Guid CharacterId { get; init; }

    public required string Message { get; init; }

    public required string Status { get; init; }

    public static CompanyApplicationDto Create(CompanyApplication source) => new()
    {
        ApplicationId = source.Id.Value,
        CharacterId = source.CharacterId.Value,
        Message = source.Message,
        Status = source.Status.ToString(),
    };

    public static CompanyApplicationDto Create(SubmitApplicationResult.Submitted source, SubmitApplicationRequestDto request) => new()
    {
        ApplicationId = source.ApplicationId.Value,
        CharacterId = request.CharacterId,
        Message = request.Message,
        Status = nameof(CompanyApplicationStatus.Pending),
    };
}
