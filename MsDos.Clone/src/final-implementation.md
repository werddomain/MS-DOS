# Plan d'implémentation final — Émulateur MS-DOS complet

Ce document décrit toutes les tâches nécessaires pour transformer le clone MS-DOS actuel en un émulateur complet capable d'installer et d'exécuter le vrai MS-DOS à partir d'une image disquette.

---

## État actuel du projet

### ✅ Composants fonctionnels
| Composant | Fichier | État |
|-----------|---------|------|
| CPU 8086/8088 + 386 extensions | `Cpu/Cpu8086.cs` | Complet (2133 lignes, tous opcodes 0x00-0xFF + 0x0F) |
| Registres 8/16/32 bits | `Cpu/Registers.cs` | Complet |
| Drapeaux CPU | `Cpu/CpuFlags.cs` | Complet |
| Bus mémoire 1 Mo | `Memory/MemoryBus.cs` | Complet |
| Bus I/O 64K ports | `Memory/IOPortBus.cs` | Complet |
| Snapshots mémoire | `Memory/MemorySnapshot.cs` | Complet |
| Contrôleur d'interruptions | `Interrupts/InterruptController.cs` | Complet |
| PIC 8259A (maître+esclave) | `Interrupts/PicController.cs` | Complet |
| PIT 8253/8254 (3 canaux) | `Interrupts/PitTimer.cs` | Complet |
| Service vidéo INT 10h | `Interrupts/BiosVideoService.cs` | 90% (texte + CGA + VGA 13h) |
| Service disque INT 13h | `Interrupts/BiosDiskService.cs` | Complet |
| Service clavier INT 16h | `Interrupts/BiosKeyboardService.cs` | Complet |
| IRQ clavier INT 09h | `Interrupts/BiosKeyboardIrqHandler.cs` | ✅ Complet (shifted, ctrl, alt, toggles) |
| Services divers INT 11h/12h/1Ah | `Interrupts/BiosMiscService.cs` | Complet |
| Service souris INT 33h | `Interrupts/BiosMouseService.cs` | Basique |
| Timer INT 08h | `Interrupts/BiosTimerService.cs` | Complet |
| Police CGA 8x8 | `Interrupts/CgaFont8x8.cs` | Complet |
| Registres CGA/VGA | `Interrupts/CgaRegisterState.cs` | Complet |
| Noyau DOS INT 21h | `Dos/DosKernel.cs` | 97% (~2100 lignes, EXEC réel) |
| Gestionnaire mémoire MCB | `Dos/MemoryManager.cs` | Complet |
| Chargeur COM/EXE | `Dos/BinaryLoader.cs` | Complet |
| Shell COMMAND.COM | `Dos/CommandShell.cs` | 90% |
| Lecteur FAT12/FAT16 | `Dos/DiskImageLoader.cs` | Complet |
| Écrivain FAT12 | `Dos/DiskImageWriter.cs` | Complet |
| Contrôleur disquette | `Dos/FloppyDriveController.cs` | Basique (gestion slots) |
| Machine principale | `DosMachine.cs` | ✅ Complet (hardware intégré + boot natif) |

---

## Tâches d'implémentation

### Phase 1 — Hardware critique pour le boot réel (P0)

#### 1.1 ✅ Contrôleur DMA 8237A
- **Fichier**: `Hardware/DmaController.cs`
- **Description**: Émulation du contrôleur DMA Intel 8237A nécessaire pour les transferts disquette
- **Fonctionnalités**:
  - [x] 4 canaux DMA (canal 2 = disquette)
  - [x] Registres d'adresse de base et compteur
  - [x] Registres de page (ports 0x81-0x83)
  - [x] Modes de transfert (single, block, demand, cascade)
  - [x] Commandes : masquage de canaux, clear flip-flop, master disable
  - [x] Calcul d'adresse physique pour transfert
  - [x] I/O ports 0x00-0x0F (DMA 1) et 0xC0-0xDF (DMA 2)

#### 1.2 ✅ Contrôleur de disquette FDC µPD765/i8272
- **Fichier**: `Hardware/Fdc765Controller.cs`
- **Description**: Émulation complète du contrôleur de disquette NEC µPD765
- **Fonctionnalités**:
  - [x] Main Status Register (MSR) — port 0x3F4
  - [x] Data Register — port 0x3F5
  - [x] Digital Output Register (DOR) — port 0x3F2
  - [x] Commandes : READ DATA, WRITE DATA, RECALIBRATE, SEEK, SENSE INTERRUPT STATUS, SPECIFY, FORMAT TRACK, READ ID
  - [x] Machine à états de commande (command → execution → result)
  - [x] Registres résultat ST0, ST1, ST2
  - [x] Géométrie CHS ↔ offset image disque
  - [x] Intégration DMA canal 2 pour transferts
  - [x] IRQ 6 en fin de transfert

#### 1.3 ✅ Boot depuis secteur de démarrage
- **Fichier**: `Hardware/BootSequence.cs`
- **Description**: Séquence de boot réaliste IBM PC
- **Fonctionnalités**:
  - [x] POST simplifié (Power-On Self Test)
  - [x] Construction de l'IVT (Interrupt Vector Table) à 0000:0000
  - [x] Construction du BDA (BIOS Data Area) à 0040:0000
  - [x] Lecture du boot sector (secteur 0) dans 0000:7C00
  - [x] Initialisation registres CPU (DL = drive de boot)
  - [x] Saut à 0000:7C00
  - [x] Support boot disquette (A:, B:) et disque dur (C:)

#### 1.4 ✅ Mémoire vidéo mappée
- **Fichier**: `Hardware/VideoMemoryMapper.cs`
- **Description**: Mapping mémoire vidéo pour accès direct par les programmes
- **Fonctionnalités**:
  - [x] CGA texte : B800:0000 (4000 octets = 80×25×2)
  - [x] MDA texte : B000:0000
  - [x] Format : octet caractère + octet attribut
  - [x] Interception lectures/écritures dans la zone B800
  - [x] Synchronisation avec IGraphicsRenderer
  - [x] Support multi-pages vidéo (8 pages)

#### 1.5 ✅ Contrôleur CMOS/RTC amélioré
- **Fichier**: `Hardware/CmosRtc.cs`
- **Description**: Contrôleur CMOS/RTC complet (actuellement stub inline dans DosMachine)
- **Fonctionnalités**:
  - [x] Registres horloge temps réel (secondes, minutes, heures, jour, mois, année)
  - [x] Registres de statut A/B/C/D
  - [x] Registres de configuration (type disquette, mémoire de base, équipement)
  - [x] Alarme programmable
  - [x] Format BCD ou binaire selon registre de statut B
  - [x] 128 octets de CMOS RAM

### Phase 2 — Compatibilité logicielle (P1)

#### 2.1 ✅ Tables clavier shifted/Ctrl/Alt (INT 09h)
- **Fichier**: Modifier `Interrupts/BiosKeyboardIrqHandler.cs`
- **Description**: Tables complètes de conversion scancode → ASCII
- **Fonctionnalités**:
  - [x] Table unshifted (existante)
  - [x] Table shifted (majuscules, symboles : !@#$%^&*())
  - [x] Table Ctrl (Ctrl+A → 0x01, etc.)
  - [x] Table Alt (scan codes spéciaux)
  - [x] Gestion Caps Lock / Num Lock
  - [x] Combinaisons spéciales (Ctrl+Alt+Del → reboot)

#### 2.2 ✅ EXEC réel (INT 21h/4Bh)
- **Fichier**: Modifier `Dos/DosKernel.cs`
- **Description**: Exécution réelle de sous-programmes COM/EXE
- **Fonctionnalités**:
  - [x] Sauvegarder le contexte parent (SS:SP, registres)
  - [x] Allouer mémoire MCB pour le processus enfant
  - [x] Créer PSP pour le processus enfant
  - [x] Charger le binaire (COM ou EXE avec relocations)
  - [x] Configurer SS:SP et CS:IP du processus enfant
  - [x] Subfunction 00h : Load & Execute
  - [x] Subfunction 01h : Load overlay
  - [x] Subfunction 03h : Load (overlay, no PSP)
  - [x] Restauration contexte parent au retour (INT 21h/4Ch)

#### 2.3 ✅ Émulation PC Speaker
- **Fichier**: `Hardware/PcSpeaker.cs`
- **Description**: Émulation du haut-parleur PC via PIT canal 2
- **Fonctionnalités**:
  - [x] Suivi de l'état gate + speaker enable (port 0x61 bits 0-1)
  - [x] Calcul fréquence depuis PIT canal 2 diviseur
  - [x] Événement OnToneChanged pour le frontend
  - [x] Support beep simple (fréquence + durée)

### Phase 3 — Améliorations (P2)

#### 3.1 ◻️ Opérations FCB (INT 21h/0Fh-28h)
- **Fichier**: Modifier `Dos/DosKernel.cs`
- **Description**: Support des File Control Blocks hérités
- **Fonctionnalités**:
  - [ ] 0Fh Open File (FCB)
  - [ ] 10h Close File (FCB)
  - [ ] 11h/12h Find First/Next (FCB)
  - [ ] 14h/15h Sequential Read/Write (FCB)
  - [ ] 16h Create File (FCB)
  - [ ] 21h/22h Random Read/Write (FCB)
  - [ ] 27h/28h Random Block Read/Write (FCB)
  - [ ] Structure FCB : drive, filename 8.3, block, record size

#### 3.2 ◻️ Redirection I/O dans le shell
- **Fichier**: Modifier `Dos/CommandShell.cs`
- **Description**: Support de la redirection et des pipes
- **Fonctionnalités**:
  - [ ] `>` redirection sortie (écraser)
  - [ ] `>>` redirection sortie (append)
  - [ ] `<` redirection entrée
  - [ ] `|` pipe entre commandes
  - [ ] `2>` redirection erreur standard
  - [ ] Fichier temporaire pour pipes

#### 3.3 ◻️ x87 FPU basique
- **Fichier**: `Cpu/Fpu8087.cs`
- **Description**: Émulation basique du coprocesseur mathématique
- **Fonctionnalités**:
  - [ ] Pile de registres ST(0)-ST(7)
  - [ ] FLD, FST, FSTP (chargement/stockage)
  - [ ] FADD, FSUB, FMUL, FDIV
  - [ ] FCOM, FCOMP (comparaison)
  - [ ] FABS, FCHS, FSQRT
  - [ ] FINIT, FCLEX
  - [ ] Status word et control word

#### 3.4 ◻️ Modes vidéo EGA/VGA étendus
- **Fichier**: Modifier `Interrupts/BiosVideoService.cs`
- **Description**: Support des modes graphiques supplémentaires
- **Fonctionnalités**:
  - [ ] Mode 0Dh : EGA 320×200 16 couleurs
  - [ ] Mode 0Eh : EGA 640×200 16 couleurs
  - [ ] Mode 10h : EGA 640×350 16 couleurs
  - [ ] Mode 12h : VGA 640×480 16 couleurs
  - [ ] Modes planaires (4 plans de bits)
  - [ ] Registres séquenceur et contrôleur graphique

#### 3.5 ◻️ Timestamps réels pour fichiers
- **Fichier**: Modifier `Dos/DosKernel.cs`, `Dos/DiskImageLoader.cs`
- **Description**: Horodatage correct des fichiers
- **Fonctionnalités**:
  - [ ] Lecture timestamp depuis entrée répertoire FAT
  - [ ] Écriture timestamp lors de la création/modification
  - [ ] Format DOS packed date/time (INT 21h/57h)
  - [ ] Propagation aux résultats FindFirst/FindNext

#### 3.6 ◻️ Commande MEM réaliste
- **Fichier**: Modifier `Dos/CommandShell.cs`
- **Description**: Affichage mémoire basé sur la vraie chaîne MCB
- **Fonctionnalités**:
  - [ ] Parcourir la chaîne MCB pour obtenir l'utilisation réelle
  - [ ] Afficher mémoire conventionnelle (640K)
  - [ ] Afficher blocks alloués avec propriétaire
  - [ ] Afficher mémoire libre totale et plus grand bloc

#### 3.7 ◻️ Callback souris (INT 33h)
- **Fichier**: Modifier `Interrupts/BiosMouseService.cs`
- **Description**: Invocation réelle des callbacks utilisateur souris
- **Fonctionnalités**:
  - [ ] Stocker adresse callback (INT 33h/0Ch)
  - [ ] Exécuter callback via CALL FAR quand événement souris
  - [ ] Passer paramètres dans registres (AX=événement, BX=boutons, CX=x, DX=y)

### Phase 4 — Avancé (P3)

#### 4.1 ◻️ Préfixe d'adressage 0x67
- **Fichier**: Modifier `Cpu/Cpu8086.cs`
- **Description**: Support du préfixe de taille d'adresse 32 bits
- **Fonctionnalités**:
  - [ ] Décodage ModR/M 32 bits (SIB byte)
  - [ ] Modes d'adressage EAX-based
  - [ ] Combinaison avec préfixe 0x66

#### 4.2 ◻️ Mode protégé basique (286+)
- **Fichier**: `Cpu/ProtectedMode.cs`
- **Description**: Support minimal du mode protégé
- **Fonctionnalités**:
  - [ ] GDT (Global Descriptor Table)
  - [ ] LDT (Local Descriptor Table)
  - [ ] IDT (Interrupt Descriptor Table)
  - [ ] Registres CR0-CR4
  - [ ] Transition mode réel ↔ mode protégé
  - [ ] Segments avec limites et droits d'accès

#### 4.3 ◻️ Support réseau (BIOS INT 14h)
- **Fichier**: Modifier `Interrupts/BiosMiscService.cs`
- **Description**: Émulation port série pour transfert de fichiers
- **Fonctionnalités**:
  - [ ] Buffer circulaire TX/RX
  - [ ] Configuration vitesse/parité/bits
  - [ ] Bridge vers WebSocket pour accès réseau

#### 4.4 ◻️ Ajout d'un clavier virtuel dans blazor et winform
- S'assurer de pouvoir mettre un modifier (shift, ctrl ou alt) a une touche envoyer
- Ajouter les combinaisons de touches standard comme ctrl+alt+del



---

## Architecture des nouveaux fichiers

```
MsDos.Core/
├── Hardware/           ← NOUVEAU DOSSIER
│   ├── DmaController.cs        ← Phase 1.1
│   ├── Fdc765Controller.cs     ← Phase 1.2
│   ├── BootSequence.cs         ← Phase 1.3
│   ├── VideoMemoryMapper.cs    ← Phase 1.4
│   ├── CmosRtc.cs              ← Phase 1.5
│   └── PcSpeaker.cs            ← Phase 2.3
├── Cpu/
│   ├── Cpu8086.cs              ← Modifié (Phase 4.1)
│   └── Fpu8087.cs              ← Phase 3.3
├── Interrupts/
│   ├── BiosKeyboardIrqHandler.cs ← Modifié (Phase 2.1)
│   └── BiosVideoService.cs      ← Modifié (Phase 3.4)
├── Dos/
│   ├── DosKernel.cs            ← Modifié (Phase 2.2, 3.1, 3.5)
│   └── CommandShell.cs         ← Modifié (Phase 3.2, 3.6)
└── DosMachine.cs               ← Modifié (intégration)
```

---

## Ordre d'implémentation recommandé

```
Phase 1.1 (DMA)          ─┐
Phase 1.5 (CMOS/RTC)     ─┤─→ Phase 1.2 (FDC) ─→ Phase 1.3 (Boot) ─→ TEST BOOT RÉEL
Phase 1.4 (Video Mem)    ─┘
Phase 2.1 (Keyboard)     ─┐
Phase 2.2 (EXEC)         ─┤─→ TEST PROGRAMMES DOS
Phase 2.3 (Speaker)      ─┘
Phase 3.x                ─────→ COMPATIBILITÉ AVANCÉE
Phase 4.x                ─────→ SUPPORT ÉTENDU
```

---

## Critère de succès
L'émulateur sera considéré comme « complet » quand :
1. ✅ On peut booter une image disquette MS-DOS 2.0 depuis le secteur de boot
2. ✅ COMMAND.COM s'exécute et affiche le prompt `A:\>`
3. ✅ Les commandes internes (DIR, TYPE, COPY) fonctionnent
4. ✅ On peut exécuter des programmes .COM et .EXE depuis le DOS natif
5. ✅ Le clavier fonctionne avec toutes les touches (shift, ctrl, alt)
6. ◻️ Les programmes graphiques CGA fonctionnent (mode 320×200)
7. ◻️ Le son via PC speaker fonctionne (BEEP)
