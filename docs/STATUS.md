# Open Burning Suite — Status do Projeto

> Atualizado: Jun 2026
> Branch ativa: `feat/emoji-to-icon-replacement`

## ✅ Concluído

### 🎨 UX — Emoji para PNG Icons (100%)
- **Branch:** `feat/emoji-to-icon-replacement`
- **76 ícones** em `Assets/Icons/16/` e `Assets/Icons/50/` (icons8 + fluentui)
- **16 views** com emoji substituído por `<Image>` (~315 ocorrências):

| View | Emoji | Status |
|------|-------|--------|
| MainWindow | 31 | ✅ |
| AdvancedView | 9 | ✅ |
| DiscInfoView | 8 | ✅ |
| BlankWizardView | 2 | ✅ |
| CopyWizardView | 4 | ✅ |
| DiscoverView | 6 | ✅ |
| VerifyView | 7 | ✅ |
| ReadView | 7 | ✅ |
| BuildView | 10 | ✅ |
| WriteView | 14 | ✅ |
| AudioWizardView | 27 | ✅ |
| VideoWizardView | 80 | ✅ |
| DataWizardView | 28 | ✅ |
| GameWizardView | 19 | ✅ |
| SettingsView | 28 | ✅ |
| HelpView | 52 | ✅ |

- **Infraestrutura:** `IconHelper.cs`, `IconSourceExtension`, `IconTextBlock`, `IconButton`, `IconConverter`, `.csproj` com `AvaloniaResource`
- **Logger:** `Logger.cs` + try-catch em `Program.cs`/`App.axaml.cs` → logs em `bin/Debug/net8.0/logs/obs_*.log`
- **Build:** 0 erros, 0 warnings

### 🔬 Media Info — Painel Disc Info (100%)
- **Branch:** `feat/disc-info-panel` (merged via PR #1)
- Popular `TxtMediaId` com dados reais do disco
- MID, fabricante, camadas, write speeds

### 🔧 CHD — Bug routing (B1 corrigido)
- `BurnService.cs:59` — `.CHD` adicionado ao routing check

## 🔄 Em Andamento

*(nada no momento)*

## 📋 Pendente

### 🔥 CHD — Completar suporte
| # | Tarefa | Prioridade |
|---|--------|-----------|
| 1 | `ConvertChdToBinCueAsync` — adicionar `IProgress<int>` | média |
| 2 | CHD como formato de saída (`chdman createcd`) | baixa |
| 3 | CHD no VerifyService | baixa |
| 4 | Suporte CHD DVD/HD/Blu-ray (`extracthd`/`extractld`) | baixa |
| 5 | Exibir metadados do CHD (`chdman info`) | baixa |
| 6 | Validar BIN/CUE extraído | baixa |

### 🔬 Media Info — Expansão
| # | Tarefa | Prioridade |
|---|--------|-----------|
| 8 | Adicionar MID, Manufacturer, Write Speeds no painel | baixa |
| 9 | Velocidades reais por mídia | baixa |
| 10 | Buffer do drive, firmware, serial | baixa |

### 🛡️ Perfis de Proteção (CloneCD-style)
| # | Tarefa | Prioridade |
|---|--------|-----------|
| 13-16 | SafeDisc, SecuROM, StarForce etc. | baixa |

### 📝 Editor ISO inline
| # | Tarefa | Prioridade |
|---|--------|-----------|
| 17-19 | IsoEditorService, TreeView, drag & drop | baixa |

### 📦 Fila de Gravação
| # | Tarefa | Prioridade |
|---|--------|-----------|
| 20-22 | BurnQueueService, UI, multi-drive | baixa |

### 💾 Salvar/Carregar Projeto (.obsproject)
| # | Tarefa | Prioridade |
|---|--------|-----------|
| 23-24 | Serializar/restaurar config | baixa |

### 🚀 USB Bootável
| # | Tarefa | Prioridade |
|---|--------|-----------|
| 25-27 | Detectar USB, escrever raw, wizard | baixa |

### 📋 Qualidade de Vida
| # | Tarefa | Prioridade |
|---|--------|-----------|
| 28 | Lista de arquivos recentes | baixa |
| 29 | Drag & drop .iso/.chd na MainWindow | baixa |
| 30 | Layer break picker gráfico (dual-layer DVD) | baixa |
| 31 | Separar `FormatHelper.GetImageType()` | baixa |

### 🔒 Segurança / Admin
| # | Tarefa | Prioridade |
|---|--------|-----------|
| R1-R3 | Alternativas ao requireAdministrator | baixa |

## 🐛 Bugs Conhecidos
- B1: ✅ corrigido (CHD routing)

## Arquitetura
- .NET 8 + Avalonia UI 11.3.12
- BIN/CUE formato intermediário universal
- Parsers estáticos por formato
- Tools externas: ffmpeg, chdman

## Referências
- [Upstream](https://github.com/SvenGDK/OpenBurningSuite)
- [Fork](https://github.com/marcelofrau/OpenBurningSuite)
