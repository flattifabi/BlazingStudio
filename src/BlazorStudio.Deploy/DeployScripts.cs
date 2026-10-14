namespace BlazorStudio.Deploy;

/// <summary>Mitgelieferte Skripte (als Ressource eingebettet).</summary>
public static class DeployScripts
{
    public const string DeployScriptName = "Deploy-BlazorStudio.ps1";

    /// <summary>Pipeline-Skript, das ein Paket über den Agent ausrollt.</summary>
    public static string DeployScript
    {
        get
        {
            using var s = typeof(DeployScripts).Assembly.GetManifestResourceStream(DeployScriptName)
                ?? throw new InvalidOperationException(DeployScriptName + " fehlt in der Assembly.");
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
    }
}
