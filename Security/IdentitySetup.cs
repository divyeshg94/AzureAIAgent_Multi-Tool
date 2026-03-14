using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace AzureAIAgent.Security;

/// <summary>
/// Centralises credential creation so every component in the agent
/// uses the same identity. In local development this resolves via
/// Azure CLI (az login). In Azure it uses the resource's Managed Identity.
/// </summary>
public static class IdentitySetup
{
    public static DefaultAzureCredential CreateCredential(ILogger? logger = null)
    {
        logger?.LogInformation(
            "Resolving Azure credential via DefaultAzureCredential. " +
            "Local: Azure CLI. Azure: Managed Identity.");

        return new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeEnvironmentCredential = false,
            ExcludeWorkloadIdentityCredential = false,
            ExcludeManagedIdentityCredential = false,
            ExcludeAzureCliCredential = false
        });
    }
}
