using Yar.Core;

namespace Yar.App;

/// <summary>
/// The strip along the bottom of the window while Yar is changing something: what, how far along,
/// and a way to stop it.
/// </summary>
internal sealed class OperationBar
{
    private readonly OperationRunner _runner;
    private readonly Gtk.Revealer _revealer = Gtk.Revealer.New();
    private readonly Gtk.Label _title = Gtk.Label.New("");
    private readonly Gtk.Label _detail = Gtk.Label.New("");
    private readonly Gtk.ProgressBar _bar = Gtk.ProgressBar.New();
    private readonly Gtk.Button _cancel = Gtk.Button.NewWithLabel(T._("Cancel"));
    private bool _pulsing;

    /// <param name="onGtkThread">Progress arrives on other threads; this brings it to GTK's own.</param>
    public OperationBar(OperationRunner runner, Action<Action> onGtkThread)
    {
        _runner = runner;

        _title.Xalign = 0;
        _title.AddCssClass("heading");
        _title.Ellipsize = Pango.EllipsizeMode.End;
        _detail.Xalign = 0;
        _detail.AddCssClass("dim-label");
        _detail.AddCssClass("caption");
        _detail.Ellipsize = Pango.EllipsizeMode.End;

        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        text.Hexpand = true;
        text.Append(_title);
        text.Append(_detail);
        text.Append(_bar);
        _bar.MarginTop = 4;

        _cancel.Valign = Gtk.Align.Center;
        _cancel.OnClicked += (_, _) => _runner.CancelCurrent();

        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        row.MarginTop = 8;
        row.MarginBottom = 8;
        row.MarginStart = 12;
        row.MarginEnd = 12;
        row.Append(text);
        row.Append(_cancel);

        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        box.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        box.Append(row);
        _revealer.Child = box;
        _revealer.TransitionType = Gtk.RevealerTransitionType.SlideUp;

        runner.Changed += () => onGtkThread(Update);
        Update();
    }

    public Gtk.Widget Widget => _revealer;

    private void Update()
    {
        if (_runner.Current is not { } current)
        {
            _revealer.RevealChild = false;
            _pulsing = false;
            return;
        }

        _title.SetText(current.Waiting > 0
            ? T.F("{0} · {1} more waiting", ChangeText.Running(current.Record), current.Waiting)
            : ChangeText.Running(current.Record));
        _detail.SetText(ChangeText.Phase(current.Progress));
        _cancel.Sensitive = current.Progress.CanCancel;
        _cancel.TooltipText = current.Progress.CanCancel
            ? null
            : T._("This step can't be interrupted safely. It will be over in a moment.");
        _revealer.RevealChild = true;

        if (current.Progress.Fraction is { } fraction)
        {
            _pulsing = false;
            _bar.Fraction = Math.Clamp(fraction, 0, 1);
        }
        else if (!_pulsing)
        {
            // No percentage to show: let the bar move so it is clear Yar has not stopped.
            _pulsing = true;
            GLib.Functions.TimeoutAdd(0, 150, () =>
            {
                if (!_pulsing)
                    return false;
                _bar.Pulse();
                return true;
            });
        }
    }
}
