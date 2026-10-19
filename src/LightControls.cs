using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DiskRelease
{
    internal sealed class LightComboBox : ComboBox
    {
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            // Native dropdowns omit their display text in WM_PRINT on some
            // Windows versions. Preserve the native control and complete only
            // that print surface, used by accessibility capture and previews.
            if ((message.Msg == 0x0317 || message.Msg == 0x0318) && message.WParam != IntPtr.Zero && DropDownStyle == ComboBoxStyle.DropDownList)
            {
                using (Graphics graphics = Graphics.FromHdc(message.WParam))
                {
                    int arrow = Math.Max(SystemInformation.VerticalScrollBarWidth, (int)Math.Ceiling(18 * graphics.DpiX / 96F));
                    Rectangle text = new Rectangle(3, 1, Math.Max(0, ClientSize.Width - arrow - 6), Math.Max(0, ClientSize.Height - 2));
                    using (SolidBrush brush = new SolidBrush(BackColor)) graphics.FillRectangle(brush, text);
                    TextRenderer.DrawText(graphics, Text, Font, text, Enabled ? ForeColor : SystemColors.GrayText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
            }
        }
    }

    // These controls retain Button/Panel semantics; only their surfaces are drawn.
    internal static class LightGeometry
    {
        public static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            if (diameter <= 1) { path.AddRectangle(bounds); return path; }
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class LightPanel : Panel
    {
        public Color BorderColor = Color.FromArgb(231, 231, 231);
        public int CornerRadius = 10;
        public LightPanel() { DoubleBuffered = true; ResizeRedraw = true; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? BackColor : Parent.BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = e.Graphics.DpiX / 96F;
            using (GraphicsPath path = LightGeometry.Rounded(new RectangleF(.5F, .5F, Width - 1, Height - 1), CornerRadius * scale))
            {
                using (SolidBrush brush = new SolidBrush(BackColor)) e.Graphics.FillPath(brush, path);
                using (Pen pen = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : BorderColor)) e.Graphics.DrawPath(pen, path);
            }
        }
    }

    internal sealed class LightButton : Button
    {
        public bool Primary;
        public bool Borderless;
        public string Glyph;
        bool hovered, pressed;
        public LightButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            bool contrast = SystemInformation.HighContrast;
            Color fill = BackColor;
            Color foreground = ForeColor;
            if (Enabled && (hovered || pressed)) fill = contrast ? SystemColors.Highlight : (Primary ? Color.FromArgb(pressed ? 25 : 64, pressed ? 25 : 64, pressed ? 25 : 64) : Color.FromArgb(pressed ? 232 : 243, pressed ? 232 : 243, pressed ? 232 : 243));
            if (!Enabled) { fill = contrast ? SystemColors.Control : (Primary ? Color.FromArgb(220, 220, 220) : Color.FromArgb(249, 249, 249)); foreground = contrast ? SystemColors.GrayText : Color.FromArgb(143, 143, 143); }
            if (contrast && Enabled && (hovered || pressed)) foreground = SystemColors.HighlightText;
            e.Graphics.Clear(Parent == null ? fill : Parent.BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = e.Graphics.DpiX / 96F;
            using (GraphicsPath path = LightGeometry.Rounded(new RectangleF(.5F, .5F, Width - 1, Height - 1), 7 * scale))
            {
                using (SolidBrush brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
                if (!Borderless || contrast)
                    using (Pen pen = new Pen(contrast ? SystemColors.WindowText : (Primary ? fill : Color.FromArgb(223, 223, 223)))) e.Graphics.DrawPath(pen, path);
            }
            Rectangle textBounds = ClientRectangle;
            if (Glyph == "more")
            {
                using (SolidBrush brush = new SolidBrush(foreground))
                    for (int i = -1; i <= 1; i++) e.Graphics.FillEllipse(brush, Width / 2F + i * 5 * scale - scale, Height / 2F - scale, 2 * scale, 2 * scale);
            }
            else
            {
                if (Glyph == "refresh")
                {
                    float x = 15 * scale, y = Height / 2F;
                    using (Pen pen = new Pen(foreground, 1.3F * scale))
                    {
                        e.Graphics.DrawArc(pen, x - 5 * scale, y - 5 * scale, 10 * scale, 10 * scale, 55, 295);
                        e.Graphics.DrawLines(pen, new PointF[] { new PointF(x + 5 * scale, y - 6 * scale), new PointF(x + 5 * scale, y - 1 * scale), new PointF(x, y - 1 * scale) });
                    }
                    textBounds.X += (int)(18 * scale); textBounds.Width -= (int)(18 * scale);
                }
                TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, foreground, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), foreground, fill);
        }
    }

    internal sealed class DriveSymbol : Control
    {
        public DriveSymbol() { SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); AccessibleRole = AccessibleRole.Graphic; AccessibleName = "磁盘"; }
        protected override void OnPaint(PaintEventArgs e)
        {
            float scale = e.Graphics.DpiX / 96F;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = LightGeometry.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), 9 * scale))
            using (SolidBrush brush = new SolidBrush(SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(245, 245, 245))) e.Graphics.FillPath(brush, path);
            float cx = Width / 2F, cy = Height / 2F;
            using (Pen pen = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(95, 95, 95), 1.4F * scale))
            {
                RectangleF box = new RectangleF(cx - 10 * scale, cy - 7 * scale, 20 * scale, 14 * scale);
                using (GraphicsPath path = LightGeometry.Rounded(box, 2 * scale)) e.Graphics.DrawPath(pen, path);
                e.Graphics.DrawLine(pen, cx - 9 * scale, cy + 2 * scale, cx + 9 * scale, cy + 2 * scale);
                e.Graphics.DrawEllipse(pen, cx + 4 * scale, cy + 4 * scale, scale, scale);
            }
        }
    }
}
