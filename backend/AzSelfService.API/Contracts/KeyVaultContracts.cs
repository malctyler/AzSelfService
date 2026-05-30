namespace AzSelfService.API.Contracts
{
    public class KeyVaultCreateRequest
    {
        public string Name { get; set; } = string.Empty;
        public string ResourceGroup { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        // Add more properties as needed
    }

    public class KeyVaultValidationResponse
    {
        public bool IsValid { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class KeyVaultDeployRequest
    {
        public string Name { get; set; } = string.Empty;
        public string ResourceGroup { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        // Add more properties as needed
    }

    public class KeyVaultDeployResponse
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
