## Behoben
- **Leerer MudStack in einem Row-Stack:** Ein leerer Container in einem horizontalen Stack ist zwar hoch, aber 0 px breit. Dadurch bekam er keine Ablagefläche, und man konnte nichts hineinziehen. Leere Container werden jetzt erkannt, sobald sie in Breite **oder** Höhe zu klein zum Treffen sind.
