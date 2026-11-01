namespace QuickTranslate.Models;

public sealed record HotkeyGesture(uint Modifiers, uint VirtualKey)
{
    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;
    public const uint Windows = 0x0008;

    public static HotkeyGesture Default { get; } = new(Control | Shift, 0x54);

    public static IReadOnlyList<HotkeyKeyOption> Keys { get; } = Enumerable.Range('A', 26)
        .Select(key => new HotkeyKeyOption((uint)key, ((char)key).ToString()))
        .Concat(Enumerable.Range('0', 10).Select(key => new HotkeyKeyOption((uint)key, ((char)key).ToString())))
        .Concat(Enumerable.Range(1, 24).Select(number => new HotkeyKeyOption((uint)(0x6F + number), $"F{number}")))
        .Append(new HotkeyKeyOption(0x20, "Space"))
        .ToArray();

    public string DisplayText
    {
        get
        {
            var parts = new List<string>();
            if ((Modifiers & Control) != 0) parts.Add("Ctrl");
            if ((Modifiers & Alt) != 0) parts.Add("Alt");
            if ((Modifiers & Shift) != 0) parts.Add("Shift");
            if ((Modifiers & Windows) != 0) parts.Add("Win");
            parts.Add(Keys.FirstOrDefault(key => key.Value == VirtualKey)?.Label ?? $"0x{VirtualKey:X2}");
            return string.Join("+", parts);
        }
    }

    public string? ValidationError => Modifiers == 0 || (Modifiers & ~(Alt | Control | Shift | Windows)) != 0
        ? "请至少选择一个修饰键（Ctrl、Alt、Shift 或 Win）。"
        : !Keys.Any(key => key.Value == VirtualKey)
            ? "请选择字母、数字、功能键或空格作为主键。"
            : Modifiers == Control && VirtualKey == 0x43
                ? "不能使用 Ctrl+C，它用于读取选中文字。请换一个组合键。"
                : null;

    public static HotkeyGesture FromSettings(AppSettings settings)
    {
        var gesture = new HotkeyGesture(settings.HotkeyModifiers, settings.HotkeyVirtualKey);
        return gesture.ValidationError is null ? gesture : Default;
    }
}

public sealed record HotkeyKeyOption(uint Value, string Label);
