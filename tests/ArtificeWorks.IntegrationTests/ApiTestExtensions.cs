using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

namespace ArtificeWorks.IntegrationTests;

internal static class ApiTestExtensions
{
    /// <summary>
    /// Reads the machine-readable <c>code</c> extension from an RFC 7807
    /// ProblemDetails error body — the stable contract consumers branch on.
    /// </summary>
    public static async Task<string?> ReadProblemCodeAsync(this HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        if (problem is not null
            && problem.Extensions.TryGetValue("code", out var value)
            && value is JsonElement element)
        {
            return element.GetString();
        }
        return null;
    }
}
