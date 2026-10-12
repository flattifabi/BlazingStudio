## Neu
- **Eigene NuGet-Feeds:** Im NuGet-Manager gibt es über das Zahnrad den Bereich **Paketquellen**. Dort lassen sich interne Feeds (Azure DevOps, GitHub Packages, ProGet, Nexus, BaGet …) mit **Benutzername und Passwort bzw. Personal Access Token** sowie lokale Ordner mit `.nupkg`-Dateien hinzufügen, bearbeiten, testen, deaktivieren und entfernen.
- Gespeichert wird in deiner Benutzer-`NuGet.Config` – genau wie bei `dotnet nuget add source`. Damit funktionieren die Feeds auch bei Installieren, `dotnet restore` und in Visual Studio. Passwörter werden unter Windows verschlüsselt.
- Quellen aus einer `nuget.config` im Projekt/Repository werden angezeigt; für sie speicherst du nur deine Zugangsdaten (in der Benutzer-Config, nicht im Repository).
- Die Suche läuft über alle aktiven Quellen gleichzeitig, eigene Feeds zuerst. Über die Auswahl neben „Vorabversionen“ lässt sich auf eine Quelle einschränken; bei eigenen Feeds zeigt ein leeres Suchfeld alle Pakete. Updates werden ebenfalls in allen Quellen gesucht.
- Fehler einer Quelle (z. B. falsches Passwort) werden als Hinweis angezeigt, die übrigen Quellen liefern trotzdem Ergebnisse.
