using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Type9.Input;
using Type9.Native;

namespace Type9.UI.Controls;

/// <summary>Поле записи сочетания клавиш. Пока оно в фокусе, глобальный движок ввода «отключён».</summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((HotkeyBox)d).Text = (string)e.NewValue));

    /// <summary>Разрешить кнопки мыши (Mouse4/Mouse5/колесо).</summary>
    public bool AllowMouse { get; set; } = true;

    public static event Action<bool>? CaptureChanged;

    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    public HotkeyBox()
    {
        // Подкласс не получает неявный стиль TextBox автоматически — берём его явно (иначе белое поле в тёмной теме)
        SetResourceReference(StyleProperty, typeof(TextBox));
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        FontWeight = FontWeights.Medium;
        Cursor = Cursors.Hand;
        MinWidth = 150;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        SetResourceReference(ToolTipProperty, "S.H.Tip");
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        CaptureChanged?.Invoke(true);
        Text = Core.Loc.T("H.Press");
        Themes.Anim.Pulse(this);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        CaptureChanged?.Invoke(false);
        Text = Hotkey;
    }

    private static HotkeyModifiers Mods()
    {
        var m = HotkeyModifiers.None;
        var k = Keyboard.Modifiers;
        if (k.HasFlag(ModifierKeys.Control)) m |= HotkeyModifiers.Ctrl;
        if (k.HasFlag(ModifierKeys.Alt)) m |= HotkeyModifiers.Alt;
        if (k.HasFlag(ModifierKeys.Shift)) m |= HotkeyModifiers.Shift;
        if (k.HasFlag(ModifierKeys.Windows)) m |= HotkeyModifiers.Win;
        return m;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { Finish(); return; }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        var mods = Mods();
        if (Input.Hotkey.IsModifierKey(vk))
        {
            // Если модификатор отпустят без других клавиш — назначим его самого (например, правый Ctrl)
            _pendingModifier = e.IsRepeat ? _pendingModifier : vk;
            Text = string.Join("+", mods.ToString().Split(", ")) + "+…";
            return;
        }
        _pendingModifier = 0;

        // Печатные клавиши без модификаторов сломали бы обычный набор текста
        bool printable = vk is >= 0x30 and <= 0x5A or Win32.VK_SPACE or >= 0xBA and <= 0xDF or Win32.VK_TAB or Win32.VK_RETURN or Win32.VK_BACK;
        if (printable && (mods & ~HotkeyModifiers.Shift) == HotkeyModifiers.None)
        {
            Text = Core.Loc.T("H.NeedMod");
            Themes.Anim.Pulse(this);
            return;
        }
        Hotkey = new Hotkey(mods, vk).ToString();
        Finish();
    }

    private int _pendingModifier;

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (_pendingModifier != 0 && vk == _pendingModifier && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _pendingModifier = 0;
            Hotkey = new Hotkey(HotkeyModifiers.None, vk).ToString();
            Finish();
            return;
        }
        base.OnPreviewKeyUp(e);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (IsKeyboardFocused && AllowMouse && e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2 or MouseButton.Middle)
        {
            int vk = e.ChangedButton switch
            {
                MouseButton.XButton1 => Win32.VK_XBUTTON1,
                MouseButton.XButton2 => Win32.VK_XBUTTON2,
                _ => Win32.VK_MBUTTON,
            };
            Hotkey = new Hotkey(Mods(), vk).ToString();
            e.Handled = true;
            Finish();
            return;
        }
        base.OnPreviewMouseDown(e);
    }

    private void Finish()
    {
        Text = Hotkey;
        Themes.Anim.Pulse(this);
        // Уводим фокус, чтобы движок ввода снова заработал
        var scope = FocusManager.GetFocusScope(this);
        FocusManager.SetFocusedElement(scope, null);
        Keyboard.ClearFocus();
    }
}

/// <summary>RadioButton.IsChecked ↔ enum/int: ConverterParameter — значение этой кнопки.</summary>
public sealed class ValueToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null) return Binding.DoNothing;
        var s = parameter.ToString()!;
        if (targetType.IsEnum) return Enum.Parse(targetType, s, true);
        if (targetType == typeof(int)) return int.Parse(s, CultureInfo.InvariantCulture);
        return s;
    }
}

/// <summary>Индекс элемента → номер для показа (с единицы).</summary>
public sealed class IndexPlusOne : IValueConverter
{
    public static readonly IndexPlusOne Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int i ? (i + 1).ToString() : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
