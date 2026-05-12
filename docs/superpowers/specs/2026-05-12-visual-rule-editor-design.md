# Visual Rule Editor Design

## Summary

Add a WPF UserControl `RuleEditorPanel` embedded in MainWindow's right side.
Users create/edit/delete trigger rules visually — no JSON hand-editing needed.

## Components

### New: `Mapping/RuleEditorPanel.xaml` + `.cs`
Self-contained UserControl with three zones:
1. **Toolbar** — "新建规则" / "保存配置" buttons
2. **Rule list** — scrollable list, each row shows name + priority badge. Up/Down/Delete per row. Selected row highlighted.
3. **Editor form** — condition block (top) + effect block (bottom).

### Modified Files
- `MainWindow.xaml` — split main area into left (existing panels) + right (RuleEditorPanel)
- `MainWindow.xaml.cs` — expose profiles/path to editor, handle ProfileSaved → refresh
- `TriggerProfile.cs` — add `FilePath` property for save-back

## Color Rules (Dark Theme)

| Element | Background | Foreground |
|---------|-----------|------------|
| Card | #1E1E3A | #E8E8E8 |
| Input controls | #16213E | #E8E8E8 |
| Labels/secondary | (transparent) | #A0A0B0 |
| Selected row | #0F3460 @ 40% opacity | #E8E8E8 |
| Delete button hover | #F44336 | white |
| Slider track | #2A2A4A | — |
| Slider thumb | #0F3460 | — |
| Checkbox checked | #0F3460 | white |

## Data Flow

```
User edits form → MappingRule properties update in-memory
    → "保存配置" clicked → TriggerProfile.Save(path)
    → ProfileSaved event → MainWindow refreshes combo + engine
```

## Edge Cases
- **No rules**: show "暂无规则，点击新建" placeholder
- **Unsaved changes**: track dirty flag, warn on profile switch
- **Delete last rule**: disable delete button if only 1 rule
- **Reorder**: up/down buttons, first rule hides "up", last hides "down"
- **Window resize**: editor panel min width 320px, ScrollViewer on both halves
