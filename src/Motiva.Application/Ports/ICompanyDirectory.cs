namespace Motiva.Application.Ports;

public sealed record CompanyRec(Guid Id, string Name, string TimeZoneId);

public interface ICompanyDirectory
{
    Task<CompanyRec?> GetAsync(Guid companyId, CancellationToken ct);

    /// <summary>Bootstrap-only trusted creation (T03): a company is never created through the API.</summary>
    Task<bool> EnsureCreatedAsync(Guid companyId, string name, string timeZoneId, CancellationToken ct);
}
