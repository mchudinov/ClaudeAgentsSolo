namespace Web;

public sealed class Settings
{
    public string Environment { get; set; } = string.Empty;
    public AzureOpenAI AzureOpenAI { get; set; } = new Web.AzureOpenAI();
}

public sealed class AzureOpenAI
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string DeploymentNameChat { get; set; } = string.Empty;
}
