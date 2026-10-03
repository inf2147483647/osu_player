using System.Globalization;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterface;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Overlays;
using osuTK;

namespace OsuPlayer.Game.Screens.Components.Timing
{
    /// <summary>
    /// Port of osu! editor's <c>FormDiscreteAdjustmentControl</c> for the timing editor: caption and an
    /// editable number box on the left (with a value label while unfocused), the discrete adjustment pad
    /// on the right.
    ///
    /// One deliberate difference from the editor: every keystroke that parses to a number is applied
    /// immediately, instead of waiting for Enter / focus loss ("输入时立刻生效").
    /// </summary>
    public partial class TimingAdjustmentControl : CompositeDrawable
    {
        /// <summary>The live value; every accepted change is reported through <see cref="ValueChanged"/>.</summary>
        public readonly BindableDouble Current = new BindableDouble();

        /// <summary>Raised immediately whenever <see cref="Current"/> changes.</summary>
        public Action<double>? ValueChanged;

        public LocalisableString Caption { get; init; }

        public string Format { get; init; } = "0.##";

        private readonly double baseIncrement;

        private FormControlBackground background = null!;
        private OsuTextBox textBox = null!;
        private OsuSpriteText valueLabel = null!;
        private FormFieldCaption captionText = null!;
        private DiscreteAdjustmentControl adjustmentControl = null!;
        private IFocusManager focusManager = null!;
        private InputManager inputManager = null!;

        [Resolved]
        private OverlayColourProvider colourProvider { get; set; } = null!;

        public TimingAdjustmentControl(double baseIncrement)
        {
            this.baseIncrement = baseIncrement;

            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            InternalChildren = new Drawable[]
            {
                background = new FormControlBackground(),
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding
                    {
                        Vertical = 5,
                        Left = 9,
                        Right = 5,
                    },
                    Children = new Drawable[]
                    {
                        new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            Spacing = new Vector2(0f, 4f),
                            Width = 0.5f,
                            Padding = new MarginPadding
                            {
                                Right = 10,
                                Vertical = 4,
                            },
                            Children = new Drawable[]
                            {
                                captionText = new FormFieldCaption(),
                                new Container
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Children = new Drawable[]
                                    {
                                        textBox = new OsuTextBox
                                        {
                                            RelativeSizeAxes = Axes.X,
                                            // The box is hidden while unfocused, but clicking the label
                                            // should still reach it - so keep it always present.
                                            AlwaysPresent = true,
                                            CommitOnFocusLost = true,
                                            SelectAllOnFocus = true,
                                            CornerRadius = 5,
                                        },
                                        valueLabel = new TruncatingSpriteText
                                        {
                                            RelativeSizeAxes = Axes.X,
                                            Padding = new MarginPadding { Right = 5 },
                                        },
                                    },
                                },
                            },
                        },
                        adjustmentControl = new DiscreteAdjustmentControl(baseIncrement)
                        {
                            Anchor = Anchor.CentreRight,
                            Origin = Anchor.CentreRight,
                            Width = 0.5f,
                            Action = change =>
                            {
                                // 禁用态下不能赋值：osu.Framework 的 bindable 会直接抛异常。
                                if (!Current.Disabled)
                                    Current.Value += change;
                            },
                        },
                    },
                },
            };

            captionText.Caption = Caption;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            focusManager = GetContainingFocusManager()!;
            inputManager = GetContainingInputManager()!;

            textBox.OnCommit += (_, _) => background.FlashOnCommit();

            // 输入即生效：文本能解析成数字就立刻写入，不等回车或失焦。
            textBox.Current.BindValueChanged(text =>
            {
                // updateValueDisplay() 回写文本也会走到这里；禁用态下赋值会抛异常。
                if (Current.Disabled)
                    return;

                if (double.TryParse(text.NewValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                    Current.Value = parsed;
            });

            Current.BindValueChanged(_ =>
            {
                updateState();
                updateValueDisplay();
                ValueChanged?.Invoke(Current.Value);
            });

            updateState();
            updateValueDisplay();
        }

        private bool lastFocused;

        /// <summary>osu.Framework's TextBox doesn't expose its focus state as a bindable; poll it.</summary>
        protected override void Update()
        {
            base.Update();

            if (textBox.HasFocus == lastFocused)
                return;

            lastFocused = textBox.HasFocus;
            updateState();
        }

        protected override bool OnHover(HoverEvent e)
        {
            updateState();
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            base.OnHoverLost(e);
            updateState();
        }

        protected override bool OnClick(ClickEvent e)
        {
            if (!Current.Disabled)
                focusManager.ChangeFocus(textBox);

            return true;
        }

        private void updateState()
        {
            textBox.ReadOnly = Current.Disabled;
            textBox.Alpha = textBox.HasFocus ? 1 : 0;
            valueLabel.Alpha = textBox.HasFocus ? 0 : 1;

            captionText.Colour = Current.Disabled ? colourProvider.Background1 : colourProvider.Content2;
            textBox.Colour = Current.Disabled ? colourProvider.Background1 : colourProvider.Content1;
            valueLabel.Colour = Current.Disabled ? colourProvider.Background1 : colourProvider.Content1;

            if (Current.Disabled)
                background.VisualStyle = VisualStyle.Disabled;
            else if (textBox.HasFocus)
                background.VisualStyle = VisualStyle.Focused;
            else if (IsHovered || adjustmentControl.Contains(inputManager.CurrentState.Mouse.Position))
                background.VisualStyle = VisualStyle.Hovered;
            else
                background.VisualStyle = VisualStyle.Normal;
        }

        private void updateValueDisplay()
        {
            string text = Current.Value.ToString(Format, CultureInfo.InvariantCulture);

            // Never fight the caret while the user is typing.
            if (!textBox.HasFocus)
                textBox.Text = text;

            valueLabel.Text = text;
        }
    }
}
