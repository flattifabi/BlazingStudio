## Neu
- **Komponenten visuell zusammenbauen:** Elemente lassen sich im Live-Designer in jede Komponente mit Inhalt ziehen (MudStack, MudPaper, MudCard, RadzenStack …).
- **Mehrere RenderFragments:** Beim Ziehen über eine Komponente mit mehreren Inhaltsbereichen (z. B. `TitleContent`, `ChildContent`, `DialogActions`, `CardHeaderActions`) erscheint eine Leiste mit allen Bereichen. Darauf ablegen legt das Element genau dort ab. Fehlende Bereiche werden angelegt, und vorhandener Inhalt wandert automatisch in `<ChildContent>`. Wird ein Bereich leer, verschwindet er wieder.
- **Leere Container sind sichtbar:** Leere Komponenten und HTML-Container zeigen im Design-Tab eine gestrichelte Ablagefläche statt 0 px Höhe.
- **Layout ein/aus:** Neuer Schalter „Layout“ im Design-Tab. Die Seite wird entweder mit `MainLayout` (Navigation, Kopfzeile …) angezeigt oder nur für sich. Die Einstellung wird gespeichert.

## Verbessert
- Komponenten, die einen Button, Link oder Text rendern (z. B. `MudButton`, `MudText`), nehmen beim Ziehen keine Kinder mehr auf. Dadurch landet ein Button nicht mehr versehentlich in einem anderen Button.
