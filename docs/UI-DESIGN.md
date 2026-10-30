# Adjusting the Renaissance design

The whole look of Renaissance lives in **AXAML files** (Avalonia's XML UI format). You can change colours,
fonts, spacing, columns and layout there without touching any C# logic.

## Fast workflow

1. Edit an `.axaml` file under `src/Renaissance.UI`.
2. Preview it **standalone** (no DAW needed):

   ```powershell
   dotnet run --project src/Renaissance.UI                  # normal window
   dotnet run --project src/Renaissance.UI -- --compact     # the compact look used inside a DAW (1000x700)
   ```
3. When happy, build the plugin package and test in your DAW:

   ```powershell
   pwsh ./build-vst2.ps1 -Install "C:\VSTPlugins\Renaissance"
   ```
   Close the DAW first. It locks the plugin DLL.

Changes to `.axaml` files only need a rebuild of the UI. Nothing in the audio or plugin code is involved.

## Where things are

| What you want to change | File |
|---|---|
| Colours, global button / textbox / grid look | `src/Renaissance.UI/App.axaml` |
| **Smaller fonts and tighter controls inside the DAW** | `src/Renaissance.UI/Styles/Compact.axaml` |
| Scan View (folders, toolbar, search, plugin grid) | `src/Renaissance.UI/Views/ScanView.axaml` |
| Plugin View (header, patch combo, Actions menu, editor area) | `src/Renaissance.UI/Views/PluginView.axaml` |
| Params dialog (pages, search, dial cards) | `src/Renaissance.UI/Views/ParamsWindow.axaml` |
| Dial drawing (arc, knob, pointer) | `src/Renaissance.UI/Controls/DialControl.cs` (C#) |
| Main window title and size | `src/Renaissance.UI/Views/MainWindow.axaml` |

## Colours and theme (`App.axaml`)

Colours are named resources at the top of the file. Change them once and the whole app follows:

```xml
<SolidColorBrush x:Key="BgBrush"          Color="#14161A" />   <!-- window background -->
<SolidColorBrush x:Key="SurfaceBrush"     Color="#1C1F25" />   <!-- cards, grid -->
<SolidColorBrush x:Key="Surface2Brush"    Color="#242830" />   <!-- buttons, dial cards -->
<SolidColorBrush x:Key="BorderBrushSoft"  Color="#2E333D" />
<SolidColorBrush x:Key="AccentBrush"      Color="#4FD1C5" />   <!-- primary button, dial arc, progress -->
<SolidColorBrush x:Key="TextBrush"        Color="#E6E8EC" />
<SolidColorBrush x:Key="MutedBrush"       Color="#8A92A0" />
```

Views use them with `{StaticResource AccentBrush}`. To add a new colour, add a resource here and reference it.

Status colours (OK / Warning / Crashed / Missing) are in `ViewModels/PluginRowViewModel.cs` (`Green`, `Amber`, `Red`, `Grey`).

### Style classes

Styles in `App.axaml` are CSS-like: `<Style Selector="Button.primary">` applies to `<Button Classes="primary">`.
Existing classes: `primary` and `danger` (buttons), `muted` and `title` (text), `card` (borders).
Add your own the same way:

```xml
<Style Selector="Button.big"><Setter Property="FontSize" Value="16" /></Style>
...
<Button Classes="big" Content="Scan" />
```

## Fonts and sizes

* **Global font and size:** the `Window` style in `App.axaml` (`FontFamily`), and `FontSize` on `MainWindow` or any panel.
* **Inside the DAW:** `Styles/Compact.axaml` is loaded only when embedded (or with `--compact`). It sets font size
  to 12 in code (`EmbeddedController.ApplyCompactStyle`) and tightens padding. Edit the values there:

  ```xml
  <Style Selector="Button"><Setter Property="Padding" Value="10,4" /></Style>
  <Style Selector="TextBlock.title"><Setter Property="FontSize" Value="18" /></Style>
  <Style Selector="DataGrid"><Setter Property="RowHeight" Value="26" /></Style>
  ```
  For a different base font size change `window.FontSize = 12;` in `Services/EmbeddedController.cs`.
* A single element: add `FontSize="11"` or `FontWeight="SemiBold"` directly on it.

## Window size inside the DAW

The DAW editor window size is requested by the app:

* Scan View: 1000 x 700. See `MainViewModel`/`EmbeddedController` (`SendResize(1000, 700)`, `PrepareWindow`).
* Plugin View: the plugin's own editor size plus room for the header and toolbar.
  Edit `ResizeForPlugin` in `Services/EmbeddedController.cs`:

  ```csharp
  SendResize(Math.Max(760, EditorWidth + 40), Math.Max(440, EditorHeight + 150));
  ```
  If the toolbar looks cramped, raise the `+ 150`. If there is wasted space, lower it.

Reason may ignore resize requests; the first size the DAW gives is what you get.

## Display scaling (DPI)

Inside a DAW the app must use the **same scaling mode as the DAW** or sizes and positions will be wrong.
Reason 10 is DPI-unaware, so the default is `unaware`. Override with an environment variable before starting the DAW:

```
RENAISSANCE_DPI=unaware      (default; Reason 10)
RENAISSANCE_DPI=system
RENAISSANCE_DPI=permonitor
```

## Layout basics (AXAML)

* `Grid` with `RowDefinitions="Auto,*,Auto"` / `ColumnDefinitions="*,Auto"`: `Auto` = size to content, `*` = take the rest.
* `StackPanel Orientation="Horizontal" Spacing="10"` puts items in a row.
* `Margin="20"` outside space, `Padding="14"` inside space (`"left,top,right,bottom"` also works).
* Hide or show with `IsVisible="{Binding SomeBool}"`.

### Scan View columns

In `ScanView.axaml` the plugin grid columns are `DataGrid.Columns`. Reorder by moving the line, resize with `Width`
(`Width="90"` fixed, `Width="2*"` proportional), rename with `Header`. To hide one, delete it.

### Plugin View toolbar

The toolbar is the `Grid` marked `<!-- Toolbar -->` in `PluginView.axaml`. The buttons are bound to commands
(`Command="{Binding UnloadCommand}"`); you can restyle, reorder or rename them freely. The commands themselves
are in `ViewModels/PluginViewModel.cs`.

### Params dialog dial cards

In `ParamsWindow.axaml`, the `DataTemplate` inside `ItemsControl.ItemTemplate` is one parameter card.

```xml
<Border Width="112" ...>                               <!-- card size -->
  <ctl:DialControl Width="64" Height="64" ... />       <!-- dial size -->
```
Change `Width` on the card for more or fewer per row, and `Width/Height` on `DialControl` for bigger dials.

## Things to keep in mind

* Binding names such as `{Binding Title}` must match a property in the view-model. If you rename one, change both.
* A typo in AXAML shows up as a build error with a line number, or as a missing element at runtime. Check the
  newest `%LOCALAPPDATA%\Renaissance\logs\renaissance-*.log`.
* Avalonia documentation: https://docs.avaloniaui.net (styles, selectors and controls are the same as here).
