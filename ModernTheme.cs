using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Battify
{
    public static class ModernTheme
    {
        // Windows 11-like Colors (Light Theme)
        public static Color BackgroundColor = Color.FromArgb(243, 243, 243); // Mica Alt-ish
        public static Color SurfaceColor = Color.White;
        public static Color TextColor = Color.FromArgb(26, 26, 26);
        public static Color SecondaryTextColor = Color.FromArgb(96, 96, 96);
        public static Color AccentColor = Color.FromArgb(0, 103, 192); // Windows Blue
        public static Color BorderColor = Color.FromArgb(229, 229, 229);
        public static Color HoverColor = Color.FromArgb(234, 234, 234);
        public static Color PressedColor = Color.FromArgb(220, 220, 220);

        // Fonts
        public static Font HeaderFont = new Font("Segoe UI Variable Display", 14, FontStyle.Bold);
        public static Font SubHeaderFont = new Font("Segoe UI Variable Text", 10, FontStyle.Bold);
        public static Font BodyFont = new Font("Segoe UI Variable Text", 9, FontStyle.Regular);
        public static Font SmallFont = new Font("Segoe UI Variable Text", 8, FontStyle.Regular);

        // Helper to apply theme to a Form
        public static void ApplyTheme(Form form)
        {
            form.BackColor = BackgroundColor;
            form.ForeColor = TextColor;
            form.Font = BodyFont;
            form.FormBorderStyle = FormBorderStyle.FixedSingle;
            
            // Try to use Segoe UI Variable if available, fallback to Segoe UI
            try { var test = new Font("Segoe UI Variable Text", 9); }
            catch { 
                HeaderFont = new Font("Segoe UI", 14, FontStyle.Bold);
                SubHeaderFont = new Font("Segoe UI", 10, FontStyle.Bold);
                BodyFont = new Font("Segoe UI", 9, FontStyle.Regular);
                SmallFont = new Font("Segoe UI", 8, FontStyle.Regular);
            }
        }

        public static GraphicsPath GetRoundedRect(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            Size size = new Size(diameter, diameter);
            Rectangle arc = new Rectangle(bounds.Location, size);
            GraphicsPath path = new GraphicsPath();

            if (radius == 0)
            {
                path.AddRectangle(bounds);
                return path;
            }

            // Top left arc  
            path.AddArc(arc, 180, 90);

            // Top right arc  
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);

            // Bottom right arc  
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);

            // Bottom left arc 
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);

            path.CloseFigure();
            return path;
        }

        // Custom Button Control
        public class ModernButton : Button
        {
            public int BorderRadius { get; set; } = 4;

            public ModernButton()
            {
                this.FlatStyle = FlatStyle.Flat;
                this.FlatAppearance.BorderSize = 0;
                this.BackColor = SurfaceColor;
                this.ForeColor = TextColor;
                this.Size = new Size(100, 32);
                this.Cursor = Cursors.Hand;
                this.Font = BodyFont;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                // Paint parent background to simulate transparency for corners
                var parentColor = this.Parent?.BackColor ?? ModernTheme.BackgroundColor;
                
                // If parent is transparent (like CardPanel), use SurfaceColor as the visual background
                if (parentColor == Color.Transparent || parentColor.A == 0)
                {
                    parentColor = SurfaceColor;
                }

                using (var brush = new SolidBrush(parentColor))
                {
                    e.Graphics.FillRectangle(brush, this.ClientRectangle);
                }

                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                var rect = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
                var path = GetRoundedRect(rect, BorderRadius);

                // Background
                Color bgColor = SurfaceColor;
                if (this.ClientRectangle.Contains(this.PointToClient(Cursor.Position)))
                {
                    bgColor = (Control.MouseButtons == MouseButtons.Left) ? PressedColor : HoverColor;
                }
                
                using (var brush = new SolidBrush(bgColor))
                {
                    e.Graphics.FillPath(brush, path);
                }

                // Border
                using (var pen = new Pen(BorderColor))
                {
                    e.Graphics.DrawPath(pen, path);
                }

                // Text
                TextRenderer.DrawText(e.Graphics, this.Text, this.Font, this.ClientRectangle, this.ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        // Primary Button (Accent Color)
        public class PrimaryButton : Button
        {
            public int BorderRadius { get; set; } = 4;

            public PrimaryButton()
            {
                this.FlatStyle = FlatStyle.Flat;
                this.FlatAppearance.BorderSize = 0;
                this.BackColor = AccentColor;
                this.ForeColor = Color.White;
                this.Size = new Size(100, 32);
                this.Cursor = Cursors.Hand;
                this.Font = new Font(BodyFont, FontStyle.Bold);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                // Paint parent background to simulate transparency for corners
                var parentColor = this.Parent?.BackColor ?? ModernTheme.BackgroundColor;

                // If parent is transparent (like CardPanel), use SurfaceColor as the visual background
                if (parentColor == Color.Transparent || parentColor.A == 0)
                {
                    parentColor = SurfaceColor;
                }

                using (var brush = new SolidBrush(parentColor))
                {
                    e.Graphics.FillRectangle(brush, this.ClientRectangle);
                }

                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                var rect = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
                var path = GetRoundedRect(rect, BorderRadius);

                // Background
                Color bgColor = AccentColor;
                if (this.ClientRectangle.Contains(this.PointToClient(Cursor.Position)))
                {
                    bgColor = (Control.MouseButtons == MouseButtons.Left) ? ControlPaint.Dark(AccentColor) : ControlPaint.Light(AccentColor);
                }

                using (var brush = new SolidBrush(bgColor))
                {
                    e.Graphics.FillPath(brush, path);
                }

                // Text
                TextRenderer.DrawText(e.Graphics, this.Text, this.Font, this.ClientRectangle, this.ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        // Card Panel
        public class CardPanel : Panel
        {
            public int BorderRadius { get; set; } = 8;

            public CardPanel()
            {
                this.BackColor = Color.Transparent; // Allow parent background to show through corners
                this.Padding = new Padding(15);
                this.Margin = new Padding(0, 0, 0, 10);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                var rect = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
                var path = GetRoundedRect(rect, BorderRadius);

                // Background - Fill with SurfaceColor explicitly
                using (var brush = new SolidBrush(SurfaceColor))
                {
                    e.Graphics.FillPath(brush, path);
                }

                // Border
                using (var pen = new Pen(BorderColor))
                {
                    e.Graphics.DrawPath(pen, path);
                }
            }
        }

        // Toggle Switch Control
        public class ToggleSwitch : Control
        {
            private bool _isOn;
            public bool IsOn
            {
                get => _isOn;
                set
                {
                    _isOn = value;
                    Invalidate();
                    CheckedChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            public event EventHandler? CheckedChanged;

            public ToggleSwitch()
            {
                this.SetStyle(ControlStyles.UserPaint | ControlStyles.DoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
                this.Size = new Size(40, 20);
                this.Cursor = Cursors.Hand;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                var rect = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
                var path = ModernTheme.GetRoundedRect(rect, this.Height / 2);

                if (_isOn)
                {
                    using (var brush = new SolidBrush(AccentColor))
                    {
                        e.Graphics.FillPath(brush, path);
                    }
                    
                    // Draw knob on right
                    int knobSize = this.Height - 4;
                    using (var brush = new SolidBrush(Color.White))
                    {
                        e.Graphics.FillEllipse(brush, this.Width - knobSize - 2, 2, knobSize, knobSize);
                    }
                }
                else
                {
                    using (var brush = new SolidBrush(Color.FromArgb(200, 200, 200))) // Gray for off
                    {
                        e.Graphics.FillPath(brush, path);
                    }
                    using (var pen = new Pen(Color.FromArgb(180, 180, 180)))
                    {
                        e.Graphics.DrawPath(pen, path);
                    }

                    // Draw knob on left
                    int knobSize = this.Height - 4;
                    using (var brush = new SolidBrush(Color.White))
                    {
                        e.Graphics.FillEllipse(brush, 2, 2, knobSize, knobSize);
                    }
                }
            }

            protected override void OnClick(EventArgs e)
            {
                base.OnClick(e);
                IsOn = !IsOn;
            }


        }
    }
}
