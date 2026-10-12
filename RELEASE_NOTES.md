## Behoben
- **Echte Darstellung baut Projekte mit Verweisen außerhalb des Projektordners:** Für den Live-Designer wird das Projekt in einen eigenen Ordner gespiegelt. Relative Verweise zeigten dort ins Leere – z. B. `<ProjectReference Include="..\Models\Models.csproj" />`, was zu Fehlern wie „The type or namespace name 'Models' does not exist“ führte, obwohl alles da ist. Jetzt zeigen im Spiegel alle Verweise aus dem Projektordner hinaus auf die Originale:
  - `ProjectReference`, `Compile`/`Content`/`None` mit `..\`, `HintPath`, `Import`, `Exists('..\…')`
  - `Directory.Build.props`, `Directory.Build.targets` und `Directory.Packages.props` (Central Package Management) aus übergeordneten Ordnern werden eingebunden
  - `nuget.config` (z. B. mit internen Feeds) und `global.json` aus übergeordneten Ordnern werden übernommen
