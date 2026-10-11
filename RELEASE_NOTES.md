## Behoben
- Komponenten-Bibliotheken wie Radzen.Blazor blieben bei „Scanning packages for components …“ hängen. Der Scan ist jetzt robust für jede Bibliothek (getestet u. a. mit MudBlazor, Radzen, Havit, ApexCharts, Leaflet) und bleibt nie mehr stehen.
- Neue Event-Handler in `.razor.cs`-Dateien bekommen automatisch das nötige `using Microsoft.AspNetCore.Components.Web;` (z. B. für `MouseEventArgs`).
