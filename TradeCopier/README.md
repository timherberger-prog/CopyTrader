# NinjaTrader Trade Copier (erste Version)

Diese erste Version enthält die typische AddOn-Struktur für NinjaTrader:

- `Addon.cs`: Einstiegspunkt für das AddOn und Menüeintrag im Control Center.
- `Engine.cs`: Kernlogik für Konten-Synchronisation und Trade-Kopier-Logik.
- `Window.cs`: Einfache UI zur Auswahl von Lead/Follower Konten.
- `AccountSelection.cs`: View-Model für die Kontoauswahl in der UI.

## Umgesetzte Kernlogik

1. **Lead bestimmt Richtung/Größe/Protection**
   - Ausführungen des Leadkontos werden hinsichtlich Richtung und Größe auf Follower repliziert.

2. **Follower ohne eigene Protection-Verwaltung**
   - Bei Flat-Signal im Lead werden Follower aktiv geflattet.
   - Dadurch soll verhindert werden, dass doppelte Stop/Target-Logik in Lead + Followern Gegenpositionen erzeugt.

3. **Kontenliste synchron zum Control Center**
   - Die im Plugin sichtbaren Konten basieren auf den aktuell verbundenen NinjaTrader-Konten.
   - Nicht mehr verfügbare Konten werden aus der Auswahl entfernt.

## Nächste sinnvolle Schritte

- Optional: zusätzliche Filter für Execution-Quellen (z. B. Instrument-Scope) ergänzen.
- Robuste Order-Fehlerbehandlung und Retry-Logik ergänzen.
- Persistenz für Konto-Mappings (Template/Workspace) ergänzen.
- Erweiterte Filter je Instrument, Konto oder Session-Zeit hinzufügen.
