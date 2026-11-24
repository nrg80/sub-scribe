# Verträge der Quellen — Abgrenzung

Dieses Dokument legt fest, **welcher Vertrag welche Frage beantwortet** und **was keiner von ihnen darf**. Es ergänzt die Anforderungen (F-M682, F-M729 bis F-M734) und wiederholt sie nicht: die Regel steht dort, die Grenze zwischen den Verträgen steht hier.

## 1. Geltung

- **Es gibt drei Verträge.** Einen vierten gibt es nicht; eine neue Aufgabe wird einem der drei zugeordnet oder sie wird nicht gebaut.
- **Ein Auftragsvertrag** mit genau vier Aufträgen: Suche, Hole, Sende, Melde (F-M682).
- **Ein Kontobuchungsvertrag** mit drei Aufgaben: eigene Einsendungen auflisten, veröffentlichte Einsendungen auflisten, eine Einsendung zurückziehen (F-M729).
- **Ein Selbstauskunftsvertrag** mit zwei Aufgaben: wie die Quelle dasteht, und wie viel Kontingent heute noch frei ist (F-M730, F-M733).
- **Jede Quelle darf einen, zwei oder drei davon erfüllen.** Der Auftragsvertrag ist Pflicht, die beiden anderen sind freiwillig. Wer sie nicht erfüllt, fehlt in der jeweiligen Liste — es gibt keine leere Pflichtantwort.
- **Die Verträge sind voneinander unabhängig.** Kein Vertrag setzt einen anderen voraus; keine Aufgabe wird über einen fremden Vertrag beantwortet.

## 2. Der Auftragsvertrag — die Arbeit an einem Untertitel

| Auftrag | Frage | Antwort |
| --- | --- | --- |
| Suche | Welche Untertitel hast du zu diesem Titel? | Kandidaten mit undurchsichtigem Griff, Sprache, Releasename, Behälter-Zugehörigkeit |
| Hole | Gib mir diesen Untertitel. | Bytes, Dateiname, fremde Kennung, Inhalts-Hash |
| Sende | Nimm diesen Untertitel (einzeln oder als Behälter). | angenommen · abgelehnt · beschäftigt · fehlgeschlagen (F-M685) |
| Melde | Dieser Eintrag ist defekt. | angenommen · abgelehnt · beschäftigt · fehlgeschlagen |

- **Der Sende-Auftrag trägt die Titel-Frage mit** (F-M734): fehlt der Quelle ihr eigener Titel-Eintrag, kann der Auftrag verlangen, dass sie ihn auf ihrer Seite sucht und anlegt. Die Antwort meldet, was geschah — bereit, angelegt, angelegt ohne lesbare Kennung, führt den Titel schon ohne Kennung, nicht auffindbar, nicht fragbar, ohne Urteil — und nennt die Kennung, wenn es eine gibt.
- **Ein Trockenlauf legt nichts an und entscheidet nichts.** Fehlt in einem Trockenlauf der Titel, lautet die Antwort „nicht fragbar"; sie ist kein Nein.
- **Der Sende-Auftrag sendet nichts, solange der Titel nicht steht.** Deshalb darf der Ablauf ihn wiederholen, ohne einen zweiten Upload zu erzeugen.
- **Der Auftragsvertrag kennt kein Konto.** Auflisten und Zurückziehen sind nicht Teil von ihm, auch nicht als fünfter Auftrag.

## 3. Der Kontobuchungsvertrag — das Verzeichnis des Kontos

- **Nur Quellen mit eigenem Konto erfüllen ihn.** Eine Quelle ohne Konto fehlt in der Liste; sie antwortet nicht „kann ich nicht".
- **Zwei Listen, zwei Griffe.** Eine veröffentlichte Einsendung trägt den Griff zum Zurückziehen und daneben den Griff ihres veröffentlichten Eintrags; die beiden Zahlenkreise sind nicht derselbe (F-M435). Eine offene Einsendung trägt nur den ersten.
- **Der Zustand ist gröber als jede Quelle:** offen, angenommen, abgelehnt — beim Ablehnen mit dem Klartext der Quelle daneben. Zustände, die nur eine Seite kennt, gehen nicht über die Grenze.
- **Der Ausgang des Zurückziehens ist der des Sendens.** Vier Ausgänge, kein zweites Vokabular (F-M685).
- **Der Kontobuchungsvertrag beantwortet keine Erreichbarkeits- und keine Kontingentfrage.** Er führt ein Verzeichnis, er gibt keine Selbstauskunft.

## 4. Der Selbstauskunftsvertrag — wie die Quelle dasteht

- **Jede Quelle erfüllt ihn**, auch eine ohne Konto: „bin ich erreichbar" weiß sie selbst am besten.
- **Die Quelle setzt ihre Lichter selbst.** Schlüssel, Beschriftung, Ampel und Klartext kommen von ihr; der Aufrufer kennt keine Quelle beim Namen und malt nur, was zurückkommt (F-M730).
- **Das Kontingent wird live gelesen, nicht zwischengespeichert.** Der Lauf unterscheidet daran kurzfristige Drosselung von der Tagesgrenze (F-M238, F-M733).
- **„Ich führe kein Kontingent" ist eine gültige Antwort** — keine erfundenen Zahlen, kein Ausbleiben.

## 5. Gemeinsame Grenzregeln

1. **Kein Vertrag kennt den Bestand.** Keine Datenbank, kein Verzeichnis, keine unserer Kennungsformen, keine Zeile unserer Ablage.
2. **Was hereingereicht wird, wird hereingereicht.** Netzverbindung, Zwischenspeicher für Archive, Protokoll und Einstellungen kommen von aussen; keine Engine legt sich eine eigene Netzverbindung an.
3. **Über die Grenze gehen nur neutrale Daten:** Sprache in einer einheitlichen Schreibweise, Zeitpunkte in UTC, Klartext der Quelle, undurchsichtige Griffe. Keine Adressen, keine Zugangsdaten, keine fremde Kennungsform.
4. **Der Quellenname ist ein Datenwert, keine Weiche.** Er steht in der Ablage, in der Herkunftszeile und auf der Kachel; es wird nicht nach ihm entschieden.
5. **Fähigkeiten statt Namensvergleiche.** Ob eine Quelle hochladen, melden, Behälter annehmen, einen Titel anlegen oder ein Kontingent führen kann, sagt sie über ihre Fähigkeiten — nicht ihr Name (F-M682, F-M734).
6. **Ein Vertrag ist eine Zusage, keine Pflicht zur Vollständigkeit.** Wer eine Aufgabe nicht kann, erfüllt den Vertrag nicht; er tut nicht so, als könnte er sie.
7. **Wer welche Sprache annimmt, sagt die Quelle** — in beide Richtungen und mit der Angabe, ob sie eine geschlossene Liste führt. „Ich führe keine Liste" ist eine eigene Auskunft und **nicht** dasselbe wie eine leere Liste, die „ich kann nichts" hieße (F-M735).

## 6. Wer in den Bestand schreibt

- **Der Ablauf schreibt, die Quelle nie.** Gespeicherte Kennungen, Marken, Sperren und Zähler gehören dem Ablauf.
- **Was die Quelle zurückgibt, ist ein Vorschlag mit Beleg:** eine Kennung, ein Zustand, ein Klartext. Erst der Ablauf entscheidet, ob und wo sie in den Bestand geht (F-M508, F-M734).
- **Eine Sperre ist Ablaufwissen.** Dass ein Titel nach vier Fehlversuchen dauerhaft geschlossen bleibt (F-M608), kennt nur der Ablauf; die Quelle bekommt davon nichts zu sehen.

## 7. Zuordnung: welche Frage gehört wohin

| Frage | Zuständig | Nie zuständig |
| --- | --- | --- |
| Welche Untertitel hast du zu diesem Titel? | Auftragsvertrag (Suche) | Kontobuchung, Selbstauskunft |
| Nimm diesen Untertitel | Auftragsvertrag (Sende), samt Titel-Anlage | Kontobuchung |
| Was liegt noch bei meinem Konto? | Kontobuchung | Auftragsvertrag |
| Zieh diesen Eintrag zurück | Kontobuchung | Auftragsvertrag |
| Bist du erreichbar, welche Lampe gilt? | Selbstauskunft | Auftragsvertrag |
| Wie viel ist heute noch frei? | Selbstauskunft (live) | Kontobuchung, Auftragsvertrag |
| Wie heißt du in einer Zeile? | Fähigkeiten | alle drei Verträge |
| Was geschah mit dem Titel? | Auftragsvertrag (Antwort des Sendens) | Kontobuchung |
| Wie viele Seiten hat mein Konto? | Kontobuchung (Tiefe der Frage) | Selbstauskunft |

## 8. Aufgaben, die in keinem Vertrag stehen

- **Titel anlegen** ist kein eigener Auftrag, sondern ein Feld des Sendens (F-M734).
- **Kontingent** ist keine Buchhaltung des Kontos, sondern eine Selbstauskunft (F-M733).
- **Diagnose am Konto** (seitenweise Zählung der eigenen Einsendungen) benutzt die Kontobuchung mit einer Seitentiefe von eins — sie erfindet keinen eigenen Weg (F-M729).
- **Statusampen** benutzen die Selbstauskunft; sie bauen keine eigene Verbindung zur Quelle (F-M730).
- **Der Kurzname einer Quelle** ist eine Fähigkeit, keine Bedingung im Ablauf (F-M731).
- **Die Sprachtafel einer Seite** ist ein Datenwert der Quelle, keine Angelegenheit des Abrufs; die eine neutrale Schreibweise des Bestandes bleibt über den Quellen (F-M557, F-M735).

## 9. Prüfungen, die die Grenze halten

- **F-M682:** keine Datei der Quelle nennt Ablage oder Ablauf; keine Datei des Ablaufs nennt eine Quelle beim Namen; jede Quelle wird ausdrücklich gebaut, nicht gesucht.
- **T392:** der Kontobuchungsvertrag trägt genau seine drei Aufgaben, steht neben den vier Aufträgen, und nur die Quelle mit Konto erfüllt ihn.
- **T393:** der Selbstauskunftsvertrag trägt genau seine Aufgaben, jede Quelle erfüllt ihn, und ausserhalb der Quelle liest keine Datei ein Zugangsdatum aus der allgemeinen Konfiguration.
- **T394:** der Ablauf nennt keine Quelle mehr — Kurzname aus den Fähigkeiten, Listen aus der Kontobuchung, Kontingent aus der Selbstauskunft.
- **T395:** der Sende-Auftrag trägt die Titel-Frage; die beiden Seiten, die Titel anlegen, tun es ohne unseren Bestand, und im Trockenlauf ohne Urteil.
- **T396:** jede Quelle gibt ihre Sprachauskunft her — die beiden Seiten mit geschlossener Liste, die dritte offen; der Rückweg liefert die Schreibweise des Bestandes, und der Ablauf fragt keinen Client mehr nach seiner Tafel.
