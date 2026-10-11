## Neu
- **Live-Designer:** Der Design-Tab zeigt jetzt die echte App, so wie sie später läuft: mit Layout, CSS und allen Komponenten-Bibliotheken (MudBlazor, Radzen …). Klicken wählt Elemente aus, Änderungen im Inspector oder im Code sind nach etwa einer Sekunde sichtbar, und Elemente aus der Toolbox lassen sich direkt in die Seite ziehen. Über das „Live“-Abzeichen schaltest du den Interaktionsmodus ein, startest neu oder öffnest das Log.
- **Detail-Tooltips in der Toolbox:** Wenn du länger über einer Komponente stehst, erscheinen Beschreibung, Parameter (aus der XML-Dokumentation der Bibliothek) und eine echte Live-Vorschau.
- **Navigation wie in Rider:**
  - F12 / Strg+Klick: Gehe zu Definition, auch aus `.razor`-Dateien heraus, für Markup wie `@store.Total` und `@onclick="Save"` und für Komponenten-Tags
  - Shift+F12 / Alt+F7: Verwendungen suchen, in `.cs`- und `.razor`-Dateien, inklusive Markup
  - Strg+F12: Gehe zu Implementierung (Interfaces, abgeleitete Klassen, Overrides)
  - F2 / Shift+F6: Umbenennen über die ganze Solution, inklusive `@code`-Blöcken und Markup
  - Alles auch im Rechtsklick-Menü des Editors. Für Symbole aus Bibliotheken wird angezeigt, aus welcher Assembly sie stammen.

## Verbessert
- `@inject`-Eigenschaften und C#-Ausdrücke im Markup werden jetzt von Roslyn verstanden.
- Prozesse des Live-Designers werden beim Beenden von Blazor Studio zuverlässig mit beendet.

## Behoben
- Dialoge und Hinweise, die aus dem Editor heraus geöffnet werden, erscheinen jetzt sofort.
