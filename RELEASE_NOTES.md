## Neu
- **Fehler werden beim Tippen rot unterstrichen:** In `.cs`-Dateien und in den `@code`-Blöcken von `.razor`-Dateien prüft Roslyn den Code kurz nach einer Tipp-Pause – z. B. unbekannte Namen, falsche Typen, fehlende Member, falsche Argumente oder ein vergessenes Semikolon. Fährt man mit der Maus über die Unterstreichung, steht der Compilerfehler (z. B. `CS0103: …`) da.

## Hinweis
- Die Prüfung ist eine schnelle Annäherung, kein voller Build. Damit sie keine falschen Fehler anzeigt, gilt:
  - Fehlen einer Datei Referenzen (Typ, Namespace oder Basisklasse aus einem Paket/einer RCL, die der Editor nicht kennt), werden dort nur Syntaxfehler angezeigt.
  - C# im Markup (`@onclick`, `@bind`, Komponenten-Parameter) und `@code`-Blöcke mit Razor-Markup (RenderFragment-Vorlagen) werden nicht geprüft.
  - Fehler wie fehlende Pakete oder doppelte Typen zeigt weiterhin der Build exakt an.
