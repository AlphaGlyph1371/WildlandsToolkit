# Stand der Wildlands-Formatparser

Stand: 23. September 2026

Dieses Dokument bewertet die Parser gegen das langfristige Ziel, ein Spielasset vollständig aus
einem eigenen, typisierten Objektmodell erzeugen zu können. Ein bytegenauer Roundtrip allein reicht
dafür nicht: Wenn ein Writer unbekannte Bytes oder ganze Blöcke aus einer Quelldatei übernehmen muss,
ist das Format hier weiterhin **teilweise**.

## Bewertungsmaßstab

| Status | Bedeutung |
|---|---|
| **Vollständig** | Alle Felder sind semantisch modelliert; Reader und Writer können das Asset ohne Vorlage neu erzeugen; bekannte Wildlands-Varianten werden unterstützt. |
| **Teilweise** | Es gibt einen typisierten Reader, Writer oder gezielten Editor, aber unbekannte Rohblöcke, nur eingebettete Teilobjekte, fehlende Varianten oder eine Abhängigkeit von einer Quelldatei bleiben. |
| **Kein Parser** | Der Typ ist nur über Name/Class-Hash bekannt oder kann lediglich roh extrahiert und ersetzt werden. |

## Zusammenfassung

| Umfang | Vollständig | Teilweise | Kein Parser |
|---|---:|---:|---:|
| 66 in `ResourceTypes` benannte Resource-Klassen | **0** | **21** | **45** |
| Zusätzlich identifizierte Resource-Klassen | **0** | **2** | nicht vollständig inventarisiert |

## Vollständige Corpus-Prüfung

Am 23. September 2026 lief `wlcli parseraudit <Spielordner> <report.json> --fast` über alle 24
installierten Archive: 301.994 Forge-Einträge, 296.798 gültige `.data`-Container und 5.675.199
Resources. Der Lauf endete mit **0 Fehlern**.

| Format | Geprüfte Assets | Ergebnis |
|---|---:|---|
| Mesh | 703.526 | 703.526 bytegenaue Roundtrips |
| Skeleton | 8.183 | 8.183 bytegenaue Roundtrips |
| BuildTable | 11.218 | 11.218 bytegenaue Roundtrips |
| Animation | 46.829 | 46.829 bytegenaue Roundtrips |
| Prefetch-Block | 296.798 | 296.798 bytegenaue Roundtrips |
| Cloth, SoftBodySettings, LayeredSky, GlobalMetaFile und TimeCycle | 1.182 | alle bytegenau |
| TextureMap, CompiledMip, Material, TextureSet, CamoPattern und LocalizationPackage | 955.678 | alle erfolgreich gelesen |

Die 24 `PrefetchingFileInfos`-Container bleiben strukturell identisch nach Schreiben und erneutem
Lesen; ihre Kompression wird in 23 Fällen anders, daher sind sie nicht roh bytegleich. Genau ein
Mesh mit einem bisher unbekannten Object-Pointer-Layout und ein Skeleton mit unbekanntem Modifier
werden als **opaque** Rohdaten erhalten. Acht Prefetch-Blöcke verwenden ebenfalls ein noch
unbekanntes 28-Byte-Layout. Alle diese Varianten sind bytegenau paketierbar, aber nicht editierbar,
bis ihre jeweilige Struktur dekodiert ist.

## Resource-Klassen

| Resource-Klasse | Class-Hash | Status | Was vorhanden ist / was bis „vollständig“ fehlt |
|---|---:|---|---|
| AccomplishmentManager | `0x79210545` | Kein Parser | Nur Name und Hash bekannt. |
| Animation | `0x0FA3067F` | Teilweise | Struktur, Tracks und bekannte Werte sind les- und schreibbar; Objekt-Payloads, `HeaderTail` und `TailHeader` bleiben rohe Bytes, unbekannte Kanäle bleiben unbekannt. |
| AtomReplaceSet | `0xCD4F18F6` | Kein Parser | Nur Name und Hash bekannt. |
| BuildTable | `0x22ECBE63` | Teilweise | Tabellen, Zeilen, Objekte, Referenzen, Tags und mehrere Listen werden erkannt; Schreiben und Zeilenkopien patchen die ursprünglichen Bytes. Es fehlt ein vollständiges Schema aller Spalten und Objekte sowie ein Writer ohne Vorlage. |
| Cloth | `0xE33044BA` | Teilweise | Container-Reader/Writer und bekannte Face-/Link-Payloads; Header und sonstige Objekt-Payloads bleiben roh. |
| Color | `0xA79767ED` | Kein Parser | Farben in anderen Formaten werden gelesen, aber kein Parser für eine `Color`-Resource. |
| CompiledMip | `0x1D4B87A3` | Teilweise | ID, Pixeldaten, TextureMap-Verweis und Mip-Level werden gelesen; kein unabhängiger Writer oder vollständiges Feldmodell. |
| CompiledTextureMap | `0x13237FE9` | Teilweise | Als eingebetteter Teil einer `TextureMap` teilweise gelesen; kein eigenständiges Objektmodell oder Writer. |
| DLCUniverseComponent | `0xAE34FA63` | Kein Parser | Nur Name und Hash bekannt. |
| EngineOptions | `0xB4E69FA1` | Kein Parser | Nur Name und Hash bekannt. |
| Entity | `0x0984415E` | Kein Parser | Nur Name und Hash bekannt. |
| EntityBuilder | `0x971A842E` | Kein Parser | Nur Name und Hash bekannt. |
| FloorHeightData | `0x92ADA771` | Kein Parser | Nur Name und Hash bekannt. |
| FX | `0x824A23BA` | Kein Parser | Nur Name und Hash bekannt. |
| GRNSoundSettings | `0x45421DEE` | Kein Parser | Nur Name und Hash bekannt. |
| LayeredSky | `0x24D32CF8` | Teilweise | Layer und Texeldaten sind les- und schreibbar; Prolog, Header-Enden, Padding und ein Feld mit unbekannter Bedeutung bleiben roh/unbenannt. |
| LensFlareSettings | `0x47D9CD32` | Kein Parser | Nur Name und Hash bekannt. |
| LightingDescriptor | `0xF75F2483` | Kein Parser | Nur Name und Hash bekannt. |
| LocalizationPackage | `0x6E3C9C6F` | Teilweise | Stringtabellen werden vollständig dekodiert und neu kodiert; der Editor kopiert jedoch den 41-Byte-Resource-Kopf aus einer Vorlage und besitzt keinen allgemeinen Asset-Writer. |
| LODSelector | `0x51DC6B80` | Kein Parser | Es existieren Referenzheuristiken für BuildTables, aber kein typisierter `LODSelector`-Parser. |
| Mask | `0xDF5D6C0E` | Teilweise | Ein fest erwartetes eingebettetes Mask-Objekt im Material wird traversiert; kein eigenständiges Feldmodell oder Writer. |
| Material | `0x85C817C3` | Teilweise | Grundfelder, Flags, Referenzen und einige Parameterobjekte (`TextureSelector`, `UVTransform`) werden gelesen; unbekannte Parameter brechen die strukturierte Auswertung ab, Restbytes werden nicht modelliert, Writer fehlt. |
| Mesh | `0x415D9568` | Teilweise | Sehr weit: strukturierter Reader/Writer, Plain-/Clustered-Geometrie, Draw Ranges, Bones, Skinning und mehrere Vertexlayouts einschließlich Format 16. Plattformdaten, Bone-Tables und Primitive-Descriptions sind teilweise rohe Blöcke; ein Object-Pointer-Sonderlayout wird opaque erhalten. Der Import übernimmt Metadaten aus dem Zielmesh. Daher noch keine vorlagenfreie Erzeugung aller Varianten. |
| MetaFile | `0x373C3C0F` | Kein Parser | Kein Parser für diese Resource-Klasse. Der separate Forge-Sondereintrag `GlobalMetaFile` ist unten aufgeführt. |
| MinimapTextures | `0xEE568905` | Kein Parser | Nur Name und Hash bekannt. |
| PostEffectSettings | `0x5FB26017` | Kein Parser | Nur Name und Hash bekannt. |
| PrefetchInfo | `0x14A61AA1` | Kein Parser | Kein Parser für diese Resource-Klasse. Nicht mit dem Forge-Sondereintrag `PrefetchingFileInfos` gleichzusetzen. |
| PrefetchingFileInfos | `0x714DE5E8` | Teilweise | Forge-Index, Objektblöcke und Referenzlisten können gelesen, geschrieben, angelegt, ergänzt und entfernt werden; die 3 Trailer-Bytes jeder Referenz sind noch unbenannt. Acht beobachtete Blöcke mit anderem Layout bleiben opaque erhalten. |
| PropertyControllerEntry | `0x050A3620` | Teilweise | Innerhalb der beiden TimeCycle-Formate strukturiert; kein eigenständiger Resource-Parser und nur vier bekannte Kurventypen. |
| PropertyPath | `0x3C00B2E0` | Teilweise | Innerhalb der TimeCycle-Formate strukturiert; mehrere feste/verworfene Bytes sind semantisch noch unbekannt. |
| PropertyPathNode | `0x1A8750BA` | Teilweise | Innerhalb der TimeCycle-Formate strukturiert; mehrere feste/verworfene Bytes sind semantisch noch unbekannt. |
| RawFile | `0xAC9581F2` | Kein Parser | Wird absichtlich als Rohdaten behandelt; kein semantisches Formatmodell. |
| RegionLayout | `0x17FB5AA8` | Kein Parser | Nur Name und Hash bekannt. |
| RoadPathContainer | `0x1B37E182` | Kein Parser | Nur Name und Hash bekannt. |
| RoadPathNetwork | `0x093BC3D4` | Kein Parser | Nur Name und Hash bekannt. |
| Season | `0xF7485E9F` | Kein Parser | Nur Name und Hash bekannt. |
| Skeleton | `0x24AECB7C` | Teilweise | Sehr weit: Hierarchie, Bind-Transforms, bekannte Modifier, GLB-Export und Rückimport; `TailData` bleibt roh und ein unbekannter Modifier wird opaque erhalten. |
| SmallLifeZoneSpawnDescriptor | `0x54CB8AD2` | Kein Parser | Nur Name und Hash bekannt. |
| SoundActionOnStateConfig | `0x0A3E93E1` | Kein Parser | Nur Name und Hash bekannt. |
| SoundDynEchoGlobalSettings | `0x725E4B56` | Kein Parser | Nur Name und Hash bekannt. |
| SoundInitSettings | `0x8DDA228D` | Kein Parser | Nur Name und Hash bekannt. |
| SoundMicroBehaviorTransitionConfig | `0xE910DE32` | Kein Parser | Nur Name und Hash bekannt. |
| SoundPointsConfig | `0x0A642100` | Kein Parser | Nur Name und Hash bekannt. |
| SoundRadioCommercialTrack | `0x8FAC0FE7` | Kein Parser | Nur Name und Hash bekannt. |
| SoundRadioSettings | `0xDEC252F5` | Kein Parser | Nur Name und Hash bekannt. |
| SoundRadioTrackSequence | `0x53A0488A` | Kein Parser | Nur Name und Hash bekannt. |
| SoundRadioVoiceoverTrack | `0x68295EFD` | Kein Parser | Nur Name und Hash bekannt. |
| SoundSettings | `0xD937E964` | Kein Parser | Nur Name und Hash bekannt. |
| SoundWindContextSoundSet | `0x83C8D5C8` | Kein Parser | Nur Name und Hash bekannt. |
| SplashFX | `0x755ACE14` | Kein Parser | Nur Name und Hash bekannt. |
| TagRules | `0x02071BC3` | Kein Parser | Nur Name und Hash bekannt. |
| TextureMap | `0xA2B7E917` | Teilweise | Metadaten, eingebettete Pixeldaten, gestreamte Mip-Verweise und bekannte Pixelformate werden gelesen; Import/Resize patcht eine vorhandene Resource. Mehrere Flags und Compiled-Felder sind unbenannt, ein unabhängiger Writer fehlt. |
| TextureMapSpec | `0x989DC6B2` | Kein Parser | Nur Name und Hash bekannt. |
| TextureSelector | `0x7D08460D` | Teilweise | Ausgewählte Referenzen werden als eingebetteter Materialparameter gelesen; kein vollständiges Objektmodell oder Writer. |
| TextureSet | `0xD70E6670` | Teilweise | Die 18 positionsabhängigen Texturslots und die Source-Referenz werden gelesen; Writer und Variantenmodell fehlen. |
| TimeOfDayPropertyControllerData | `0xF0C85018` | Teilweise | Reader/Writer und Textbearbeitung für Float-, Color-, Vector3- und Byte-Kurven; interne IDs und mehrere Path-Bytes werden beim Schreiben kanonisch neu erzeugt bzw. genullt, weitere Kurvenklassen fehlen. |
| UILightingSetup | `0x225C4723` | Kein Parser | Nur Name und Hash bekannt. |
| Universe | `0x98435A63` | Kein Parser | Nur Name und Hash bekannt. |
| UVTransform | `0xC52E2125` | Teilweise | U/V-Skalierung wird als eingebetteter Materialparameter gelesen; restliche Felder und Writer fehlen. |
| WeatherPropertyControllerData | `0x887004DC` | Teilweise | Dasselbe strukturierte TimeCycle-Modell wie oben, mit 100 Wetter-Schritten; dieselben Lücken bei Path-Metadaten und Kurvenvarianten. |
| WindPropertyControllerData | `0xBC116237` | Kein Parser | Nur Name und Hash bekannt. |
| World | `0xFBB63E47` | Kein Parser | Nur Name und Hash bekannt. |
| WorldAmbiance | `0x21C2D472` | Kein Parser | Nur Name und Hash bekannt. |
| WorldParticlesDescriptor | `0x8F3D5BB6` | Kein Parser | Nur Name und Hash bekannt. |
| WorldTransitionComponent | `0x07FAD9A0` | Kein Parser | Nur Name und Hash bekannt. |
| WorldTransitionPortal | `0x7C00F6AF` | Kein Parser | Nur Name und Hash bekannt. |

## Zusätzlich identifizierte Resource-Klassen

| Resource-Klasse | Class-Hash | Status | Lücke |
|---|---:|---|---|
| CamoPattern (Name des Spieltyps noch unbekannt) | `0x59F72F67` | Teilweise | Textur-ID und Anzeigename werden gelesen; übersprungene Inline-Felder und Writer fehlen. |
| SoftBodySettings | `0xF7A7E4DF` | Teilweise | 45 beobachtete Felder und Bone-Liste können gelesen/geändert/zurückgeschrieben werden; der 129-Byte-Festblock ist noch nicht vollständig semantisch benannt. |

## Container und technische Bausteine

Diese Formate sind keine normalen Resource-Klassen, sind für vollständig selbst erzeugte Assets aber
ebenso wichtig.

| Format/Baustein | Status | Begründung |
|---|---|---|
| LZO-Kompression | Vollständig für die verwendete Variante | Eigenständiger Compressor und Decompressor; keine Quelldatei erforderlich. |
| CompressedBlob | Vollständig für unterstützte Versionen | Header, Blocktabellen, Checksummen, komprimierte und unkomprimierte Blöcke werden gelesen und geschrieben. |
| `.data`-Container | Teilweise | Index, Payload und Resource-Reihenfolge sind les-/schreibbar. Resource-Header enthalten weiterhin kopierte Suffixdaten; neue Resources werden deshalb aus einer Vorlage geklont. |
| `.forge` v27 | Teilweise, weit fortgeschritten | Archive können gelesen, neu aufgebaut und als Addon neu angelegt werden; Add/Remove/Replace ist vorhanden. Mehrere Tabellenfelder und Metadaten sind nur als feste Werte bzw. kopierte Bereiche bekannt, andere Forge-Versionen fehlen. |
| GlobalMetaFile | Teilweise | Feldtypen Number, LongNumber, Text, Block und Table werden gelesen/geschrieben und ein Patch-Metafile kann neu erzeugt werden; Block-/Table-Inhalte bleiben semantisch roh. |
| PrefetchingFileInfos und Prefetch-Blöcke | Teilweise | Voller struktureller Read/Write/Create-Pfad; die Bedeutung der Referenz-Trailer ist unbekannt. |
| Scimitar-Objektheader | Teilweise | ID und Class-Hash sind klar; viele konkrete Objektkörper besitzen noch kein Schema. |
| `AnvilObjects`-Scanner | Kein Formatparser | Erkennt wahrscheinliche eingebettete Objekte heuristisch, ersetzt aber keinen typisierten Parser. |

## Austauschformate außerhalb des Spiels

GLB/glTF, OBJ, FBX und DDS sind keine spielinternen Assetformate. Sie lösen Import/Export-Aufgaben,
sagen aber nichts darüber aus, ob eine Wildlands-Resource ohne Vorlage erzeugt werden kann. Aktuell
gibt es GLB/glTF-Import und -Export für Meshes, GLB-Roundtrip für Skeletons, OBJ-Import und -Export,
FBX-Export sowie DDS-Read/Write für die unterstützten Pixelformate.

## Empfohlene Reihenfolge zum Erreichen des Ziels

1. **Mesh und Skeleton vollständig semantisieren.** Beide sind bereits weit und bringen direkt den
   größten Nutzen für Blender-Workflows. Ziel: kein `PlatformData`, `BoneTable`, `PrimitiveDescriptions`
   oder `TailData` mehr als unerklärter Kopierblock.
2. **Material, TextureSet, TextureSelector und TextureMap als zusammenhängenden Graph vervollständigen.**
   Damit werden neue Meshes samt Materialien und Texturen ohne Template möglich.
3. **Resource-Header und `.data`-Erzeugung abschließen.** Erst dann kann eine neue Resource wirklich
   ohne geklonten Header in einen Container geschrieben werden.
4. **BuildTable von Patch-Modell auf Schema/Writer umstellen.** Dafür müssen Objektklassen und
   Spaltenlayouts typisiert werden.
5. **LODSelector und Entity/EntityBuilder.** Diese schließen die Lücke zwischen neuem Modell,
   Streaming/LOD und Platzierung/Verwendung im Spiel.
6. Danach World-, FX-, Lighting- und Soundfamilien anhand eines tatsächlichen Class-Hash-Census
   priorisieren. Unbekannte Class-Hashes müssen dabei zusätzlich benannt werden; die Liste in
   `ResourceTypes` ist keine Garantie, dass alle im Spiel vorkommenden Klassen bereits bekannt sind.
