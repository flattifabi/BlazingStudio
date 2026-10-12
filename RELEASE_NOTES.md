## Behoben
- **Designer bleibt bei komplexen Apps bedienbar:** In der echten Darstellung läuft deine App im Designer. Zeigt sie die geöffnete Seite gar nicht – etwa weil sie auf eine Anmeldeseite umleitet, eine Fehlerseite oder nur eine Ladeanzeige zeigt (Routenparameter, Daten aus einem Backend, das in der Designer-Kopie nicht erreichbar ist) –, gab es nichts zum Auswählen oder Ziehen. Jetzt erkennt der Designer das:
  - Zeigt die App einige Sekunden lang kein einziges Element der geöffneten Datei, oder meldet sie sich gar nicht im Designer (z. B. Umleitung auf eine externe Anmeldung), wechselt er automatisch auf die Schnellansicht. Dort funktionieren Auswählen und Ziehen wie gewohnt.
  - Ein Hinweis oben erklärt den Grund; „Echte Darstellung erneut versuchen“ lädt die App wieder.
  - Während eines Neu-Builds und im Modus „Mit der App interagieren“ greift der Designer nicht ein.
