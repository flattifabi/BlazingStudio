## Neu: Schnellaktionen mit Strg+. / Alt+Enter (wie in Rider)
- **Strg+.** (oder **Alt+Enter**) öffnet im Code-Editor die Schnellaktionen von Roslyn – in `.cs`-Dateien und in `@code`-Blöcken von `.razor`.
- **Fehler beheben:** „Interface implementieren“ (auch explizit oder mit Dispose-Muster), „Abstrakte Klasse implementieren“, „using hinzufügen“, „Methode/Property erzeugen“ und alle weiteren Code-Fixes von Roslyn.
- **Refactorings** (auf Englisch, wie Roslyn sie benennt): Konstruktor erzeugen, Feld kapseln, `Equals`/`GetHashCode` erzeugen, lokale Variable oder Parameter einführen, Methode bzw. lokale Funktion extrahieren, temporäre Variable einsetzen, `DebuggerDisplay` ergänzen und weitere. Aktionen, die in Visual Studio einen Auswahldialog öffnen oder Dateien verschieben, sind ausgeblendet.
- Die Glühbirne erscheint automatisch, sobald in der Zeile ein Fehler oder eine Warnung steht.

## Neu: `override`-Vervollständigung
- Nach `override ` öffnet sich sofort die Liste aller überschreibbaren Member der Basisklasse (abstrakte und virtuelle Methoden, Properties, `ToString`, `Equals` …).
- Die Auswahl fügt die **komplette Methode** mit Signatur und Rumpf ein (`base.…()` bzw. `throw new NotImplementedException()`), richtig eingerückt.

## Behoben
- **RenderFragment-Slots beim Ziehen:** Ziehst du ein Element über eine Komponente mit mehreren RenderFragments (z. B. `TitleContent` und `ChildContent`), erscheint auch in der Schnellansicht wieder sofort die Slot-Leiste. Die Komponente wird hervorgehoben, der Ziel-Slot leuchtet auf, und das Element landet genau dort. Bisher gab es die Leiste nur in der echten Darstellung; seit der automatischen Schnellansicht (1.5.4) fehlte sie daher oft.
