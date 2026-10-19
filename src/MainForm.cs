using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskRelease
{
    public sealed class MainForm : Form
    {
        readonly Color ink = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(36, 36, 36);
        readonly Color muted = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(102, 102, 102);
        readonly Color accent = SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(36, 36, 36);
        readonly Color surface = SystemInformation.HighContrast ? SystemColors.Window : Color.White;
        readonly Color secondarySurface = SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(246, 246, 246);
        readonly Color line = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(229, 229, 229);
        readonly ComboBox drives = new LightComboBox();
        readonly CheckBox showAll = new CheckBox();
        readonly Button refresh = new LightButton(), scan = new LightButton();
        readonly Button close = new LightButton(), force = new LightButton(), selectAll = new LightButton(), clear = new LightButton(), admin = new LightButton();
        readonly Button more = new LightButton();
        readonly ContextMenuStrip moreMenu = new ContextMenuStrip();
        readonly ToolStripMenuItem menuForce = new ToolStripMenuItem("强制结束所选…"), menuSelectAll = new ToolStripMenuItem("全选"), menuClear = new ToolStripMenuItem("取消选择"), menuAdmin = new ToolStripMenuItem("管理员运行"), menuShowAll = new ToolStripMenuItem("显示内部盘（仅检查）");
        readonly ToolStripMenuItem menuFolders = new ToolStripMenuItem("关闭相关文件夹");
        readonly Label diskNote = new Label(), stateTitle = new Label(), stateDetail = new Label(), summary = new Label(), selectedDetail = new Label();
        readonly DataGridView grid = new DataGridView();
        readonly Panel progress = new Panel();
        readonly Label emptyTitle = new Label(), emptyNote = new Label();
        readonly Panel emptyPanel = new Panel();
        TableLayoutPanel statePanel;
        readonly Panel resultsHost = new Panel();
        LightPanel resultsFrame;
        float uiScale = 1F;
        readonly Color canvas = SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(250, 250, 250);
        readonly List<string> journal = new List<string>();
        readonly List<Occupant> occupants = new List<Occupant>();
        readonly bool preview;
        bool busy;
        List<DiskTarget> diskCache = new List<DiskTarget>();
        DiskTarget selectedDisk;

        public MainForm(bool previewMode)
        {
            preview = previewMode;
            Text = "简单查盘器";
            Font = new Font("Microsoft YaHei UI", 9.5F);
            BackColor = canvas;
            ForeColor = ink;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(900, 600);
            MinimumSize = new Size(700, 540);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BuildInterface();
            using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero)) uiScale = graphics.DpiX / 96F;
            ScaleInterface(uiScale);
            if (preview) FillPreview();
            else Shown += async delegate { await LoadDisks(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (busy && !preview)
                {
                    e.Cancel = MessageBox.Show(this, "操作仍在进行。退出后，已经发出的关闭请求仍由 Windows 处理。\r\n\r\n是否退出工具？", "退出简单查盘器", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes;
                }
            };
        }

        Label TextLabel(string text, float size, bool bold)
        {
            return new Label { Text = text, Font = new Font(Font.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular), AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        }

        static int Scaled(int value, float factor) { return (int)Math.Round(value * factor); }

        static Padding Scaled(Padding value, float factor)
        {
            return new Padding(Scaled(value.Left, factor), Scaled(value.Top, factor), Scaled(value.Right, factor), Scaled(value.Bottom, factor));
        }

        void ScaleInterface(float factor)
        {
            // A single explicit geometry pass keeps 96-DPI design units separate
            // from point-sized fonts. The app is system-DPI aware in its manifest.
            // Switching AutoScaleMode after creating controls had left table rows
            // at design pixels while GDI correctly rendered fonts at system DPI.
            if (Math.Abs(factor - 1F) < .01F) { FitResults(); return; }
            List<Control> controls = new List<Control>();
            CollectControls(this, controls);
            SuspendLayout();
            foreach (Control control in controls) control.SuspendLayout();
            foreach (Control control in controls)
            {
                control.Padding = Scaled(control.Padding, factor);
                control.Margin = Scaled(control.Margin, factor);
                TableLayoutPanel table = control as TableLayoutPanel;
                if (table != null)
                {
                    foreach (RowStyle row in table.RowStyles) if (row.SizeType == SizeType.Absolute) row.Height *= factor;
                    foreach (ColumnStyle column in table.ColumnStyles) if (column.SizeType == SizeType.Absolute) column.Width *= factor;
                }
                Rectangle bounds = control.Bounds;
                control.Bounds = new Rectangle(Scaled(bounds.X, factor), Scaled(bounds.Y, factor), Scaled(bounds.Width, factor), Scaled(bounds.Height, factor));
            }
            drives.ItemHeight = Scaled(23, factor);
            grid.RowTemplate.Height = Scaled(78, factor);
            foreach (DataGridViewColumn column in grid.Columns)
            {
                column.MinimumWidth = Math.Max(2, Scaled(column.MinimumWidth, factor));
                if (column.AutoSizeMode != DataGridViewAutoSizeColumnMode.Fill) column.Width = Math.Max(column.MinimumWidth, Scaled(column.Width, factor));
            }
            MinimumSize = new Size(Scaled(700, factor), Scaled(540, factor));
            ClientSize = new Size(Scaled(900, factor), Scaled(600, factor));
            for (int i = controls.Count - 1; i >= 0; i--) controls[i].ResumeLayout(false);
            ResumeLayout(true);
            FitResults();
        }

        static void CollectControls(Control parent, List<Control> result)
        {
            foreach (Control child in parent.Controls)
            {
                result.Add(child);
                if (!(child is DataGridView)) CollectControls(child, result);
            }
        }

        void FitResults()
        {
            if (resultsFrame == null || resultsHost.ClientSize.Height <= 0) return;
            int natural = grid.Rows.Count == 0 ? Scaled(180, uiScale) : grid.Rows.Cast<DataGridViewRow>().Sum(row => row.Height) + resultsFrame.Padding.Vertical + 1;
            resultsFrame.Height = Math.Min(resultsHost.ClientSize.Height, natural);
        }
        void StyleButton(Button button, string text, bool filled, Color color)
        {
            button.Text = text;
            button.AutoSize = false;
            button.Height = 36;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = filled ? color : surface;
            button.ForeColor = filled ? (SystemInformation.HighContrast ? SystemColors.HighlightText : Color.White) : ink;
            button.Cursor = Cursors.Hand;
            button.Margin = new Padding(0);
            button.UseVisualStyleBackColor = false;
            LightButton light = button as LightButton;
            if (light != null) light.Primary = filled;
        }

        void BuildInterface()
        {
            SuspendLayout();
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0), Padding = new Padding(0), BackColor = canvas };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            Controls.Add(layout);
            TableLayoutPanel body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(30, 18, 30, 22), Margin = new Padding(0), BackColor = canvas };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 61));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(body, 0, 0);

            TableLayoutPanel heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 0, 0, 20) };
            heading.RowCount = 1;
            heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            Label title = TextLabel("简单查盘器", 17F, true); title.Margin = new Padding(0);
            heading.Controls.Add(title, 0, 0);
            StyleButton(more, "更多", false, accent); more.Dock = DockStyle.Fill;
            ((LightButton)more).Glyph = "more"; ((LightButton)more).Borderless = true;
            more.AccessibleName = "更多操作";
            more.Click += delegate { moreMenu.Show(more, new Point(more.Width - moreMenu.Width, more.Height)); };
            heading.Controls.Add(more, 1, 0);
            body.Controls.Add(heading, 0, 0);

            LightPanel driveCard = new LightPanel { Dock = DockStyle.Fill, BackColor = surface, BorderColor = line, Padding = new Padding(17, 14, 17, 14), Margin = new Padding(0) };
            TableLayoutPanel drivePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0), Padding = new Padding(0), BackColor = surface };
            drivePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            drivePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 59));
            drivePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            drivePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
            DriveSymbol symbol = new DriveSymbol { Size = new Size(44, 44), Anchor = AnchorStyles.Left, Margin = new Padding(0) };
            drivePanel.Controls.Add(symbol, 0, 0);
            TableLayoutPanel driveCopy = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 15, 0), Padding = new Padding(0) };
            driveCopy.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            driveCopy.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            driveCopy.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            drives.DropDownStyle = ComboBoxStyle.DropDownList;
            drives.FlatStyle = FlatStyle.Flat;
            drives.Dock = DockStyle.Fill;
            drives.BackColor = surface;
            drives.ForeColor = ink;
            drives.Font = new Font(Font.FontFamily, 10.5F, FontStyle.Bold);
            drives.DrawMode = DrawMode.Normal;
            drives.FormattingEnabled = true;
            drives.AccessibleName = "当前磁盘";
            drives.Format += delegate(object sender, ListControlConvertEventArgs e)
            {
                DiskTarget disk = e.ListItem as DiskTarget;
                if (disk != null) e.Value = (string.IsNullOrWhiteSpace(disk.Label) ? disk.Model : disk.Label) + "（" + disk.Root.TrimEnd('\\') + "）" + (disk.CanEject ? "" : " · 仅检查");
            };
            drives.Margin = new Padding(0);
            drives.SelectedIndexChanged += delegate { ChangeDisk(); };
            driveCopy.Controls.Add(drives, 0, 0);
            diskNote.Dock = DockStyle.Fill;
            diskNote.Margin = new Padding(2, 0, 0, 0);
            diskNote.TextAlign = ContentAlignment.MiddleLeft;
            diskNote.ForeColor = muted;
            diskNote.Font = new Font(Font.FontFamily, 9F);
            diskNote.AutoEllipsis = true;
            driveCopy.Controls.Add(diskNote, 0, 1);
            drivePanel.Controls.Add(driveCopy, 1, 0);
            StyleButton(scan, "重新检查", false, accent); scan.Size = new Size(116, 36); scan.Anchor = AnchorStyles.Right;
            ((LightButton)scan).Glyph = "refresh";
            scan.Click += async delegate { await ScanDisk(); };
            drivePanel.Controls.Add(scan, 2, 0);
            driveCard.Controls.Add(drivePanel);
            body.Controls.Add(driveCard, 0, 1);

            // Keep all operation controls so the shared safeguards remain the single
            // source for enabling the visible buttons and secondary menu commands.
            StyleButton(refresh, "刷新磁盘", false, accent);
            refresh.Click += async delegate { await LoadDisks(); };
            StyleButton(close, "关闭所选", true, accent);
            close.Click += async delegate { await CloseSelected(PrimaryCloseRequiresForce(CheckedItems())); };
            force.Click += async delegate { await CloseSelected(true); };
            selectAll.Click += delegate { SetChecked(true); };
            clear.Click += delegate { SetChecked(false); };
            admin.Text = DiskEngine.IsAdministrator() ? "已是管理员" : "管理员运行";
            admin.Click += Elevate;
            showAll.CheckedChanged += delegate { FillDiskList(); };
            ToolStripMenuItem menuRefresh = new ToolStripMenuItem("刷新磁盘");
            menuRefresh.Click += async delegate { await LoadDisks(); };
            menuForce.Click += async delegate { await CloseSelected(true); };
            menuFolders.Click += async delegate { await CloseRelatedFolders(); };
            menuSelectAll.Text = "全选软件";
            menuSelectAll.Click += delegate { SetChecked(true); };
            menuClear.Click += delegate { SetChecked(false); };
            menuAdmin.Click += Elevate;
            menuShowAll.CheckOnClick = true;
            menuShowAll.CheckedChanged += delegate { showAll.Checked = menuShowAll.Checked; };
            ToolStripMenuItem history = new ToolStripMenuItem("操作记录");
            history.Click += delegate { ShowJournal(); };
            moreMenu.Font = Font;
            moreMenu.ShowImageMargin = false;
            moreMenu.Padding = new Padding(5);
            moreMenu.Items.AddRange(new ToolStripItem[] { menuSelectAll, menuClear, new ToolStripSeparator(), menuForce, menuFolders, new ToolStripSeparator(), menuRefresh, menuAdmin, menuShowAll, history });
            moreMenu.Opening += delegate { menuRefresh.Enabled = refresh.Enabled; };

            statePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, AutoSize = true, Margin = new Padding(0, 22, 0, 10), Padding = new Padding(0) };
            statePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 166));
            statePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            statePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            statePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            statePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 2));
            FlowLayoutPanel section = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            Label sectionName = TextLabel("占用软件", 10F, true); sectionName.AutoSize = false; sectionName.Size = new Size(76, 26); sectionName.Margin = new Padding(0);
            section.Controls.Add(sectionName);
            summary.Text = "0"; summary.AutoSize = false; summary.Size = new Size(25, 22); summary.Margin = new Padding(4, 2, 0, 0);
            summary.BackColor = surface; summary.BorderStyle = BorderStyle.FixedSingle; summary.TextAlign = ContentAlignment.MiddleCenter; summary.ForeColor = muted;
            summary.Font = new Font(Font.FontFamily, 8.5F);
            section.Controls.Add(summary);
            statePanel.Controls.Add(section, 0, 0);
            stateTitle.Font = new Font(Font.FontFamily, 9F);
            stateTitle.Text = "未检查";
            stateTitle.Dock = DockStyle.Fill;
            stateTitle.AutoEllipsis = true;
            stateTitle.TextAlign = ContentAlignment.MiddleRight;
            stateTitle.ForeColor = muted;
            stateTitle.Margin = new Padding(0);
            statePanel.Controls.Add(stateTitle, 1, 0);
            stateDetail.Text = "选择磁盘后点击“重新检查”。";
            stateDetail.Dock = DockStyle.Fill;
            stateDetail.TextAlign = ContentAlignment.MiddleLeft;
            stateDetail.AutoEllipsis = true;
            stateDetail.ForeColor = muted;
            stateDetail.Font = new Font(Font.FontFamily, 9F);
            stateDetail.Margin = new Padding(0);
            stateDetail.AccessibleName = "操作说明";
            statePanel.Controls.Add(stateDetail, 0, 1);
            statePanel.SetColumnSpan(stateDetail, 2);
            progress.Dock = DockStyle.Fill;
            progress.BackColor = accent;
            progress.Visible = false;
            progress.Margin = new Padding(0);
            statePanel.Controls.Add(progress, 0, 2);
            statePanel.SetColumnSpan(progress, 2);
            body.Controls.Add(statePanel, 0, 2);

            LightPanel results = new LightPanel { Dock = DockStyle.Top, BackColor = surface, BorderColor = line, Padding = new Padding(1, 5, 1, 5), Margin = new Padding(0) };
            resultsFrame = results;
            resultsHost.Dock = DockStyle.Fill; resultsHost.Margin = new Padding(0); resultsHost.BackColor = canvas;
            resultsHost.Resize += delegate { FitResults(); };
            resultsHost.Controls.Add(results);
            grid.Dock = DockStyle.Fill;
            grid.BackgroundColor = surface;
            grid.BorderStyle = BorderStyle.None;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AllowUserToResizeColumns = false;
            grid.RowHeadersVisible = false;
            grid.ColumnHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.AutoGenerateColumns = false;
            grid.EnableHeadersVisualStyles = false;
            grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = surface, ForeColor = ink, SelectionBackColor = SystemInformation.HighContrast ? SystemColors.Highlight : secondarySurface, SelectionForeColor = SystemInformation.HighContrast ? SystemColors.HighlightText : ink, WrapMode = DataGridViewTriState.False };
            grid.GridColor = line;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.None;
            grid.RowTemplate.Height = 78;
            grid.Margin = new Padding(0);
            grid.AccessibleName = "占用此磁盘的软件";
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Pick", HeaderText = "选择", Width = 49, Resizable = DataGridViewTriState.False });
            grid.Columns[0].HeaderCell.ToolTipText = "选择要关闭的软件";
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Software", HeaderText = "软件及占用文件", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 250, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "PID", HeaderText = "PID", Visible = false, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "可用操作", Visible = false, ReadOnly = true });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Paths", HeaderText = "占用文件", Visible = false, ReadOnly = true });
            grid.Columns.Add(new DataGridViewButtonColumn { Name = "Details", HeaderText = "详情", Text = "查看详情", UseColumnTextForButtonValue = true, Width = 44, FlatStyle = FlatStyle.Flat, ReadOnly = true });
            grid.CurrentCellDirtyStateChanged += delegate { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += delegate { UpdateButtons(); grid.Invalidate(); };
            grid.SelectionChanged += delegate { ShowSelectedDetails(); };
            grid.CellContentClick += delegate(object s, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0 && e.ColumnIndex == 5) ShowPathDetails(e.RowIndex); };
            grid.CellDoubleClick += delegate(object s, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0 && e.ColumnIndex != 0 && e.ColumnIndex != 5) ShowPathDetails(e.RowIndex); };
            grid.CellBeginEdit += delegate(object s, DataGridViewCellCancelEventArgs e) { if (e.ColumnIndex == 0 && grid.Rows[e.RowIndex].Tag is Occupant && ((Occupant)grid.Rows[e.RowIndex].Tag).Protected) e.Cancel = true; };
            grid.CellPainting += PaintResultCell;
            results.Controls.Add(grid);
            emptyPanel.Dock = DockStyle.Fill; emptyPanel.BackColor = surface; emptyPanel.Padding = new Padding(24); emptyPanel.Visible = false;
            emptyTitle.Dock = DockStyle.Top; emptyTitle.Height = 28; emptyTitle.Text = "尚未检查占用"; emptyTitle.TextAlign = ContentAlignment.BottomCenter; emptyTitle.ForeColor = ink;
            emptyNote.Dock = DockStyle.Top; emptyNote.Height = 48; emptyNote.TextAlign = ContentAlignment.TopCenter; emptyNote.ForeColor = muted; emptyNote.Font = new Font(Font.FontFamily, 9F);
            TableLayoutPanel emptyLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
            emptyLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            emptyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); emptyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78)); emptyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            Panel emptyCopy = new Panel { Dock = DockStyle.Fill };
            emptyCopy.Controls.Add(emptyNote); emptyCopy.Controls.Add(emptyTitle);
            emptyLayout.Controls.Add(emptyCopy, 0, 1); emptyPanel.Controls.Add(emptyLayout);
            results.Controls.Add(emptyPanel);
            body.Controls.Add(resultsHost, 0, 3);

            TableLayoutPanel footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(30, 18, 30, 18), Margin = new Padding(0), BackColor = surface };
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 156));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            footer.Paint += delegate(object sender, PaintEventArgs e) { using (Pen pen = new Pen(line)) e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            selectedDetail.Dock = DockStyle.Fill;
            selectedDetail.TextAlign = ContentAlignment.MiddleLeft;
            selectedDetail.AutoEllipsis = true;
            selectedDetail.ForeColor = muted;
            selectedDetail.Margin = new Padding(0);
            selectedDetail.Text = "已选择 0 个软件";
            footer.Controls.Add(selectedDetail, 0, 0);
            Label saveNote = TextLabel("关闭前请先保存工作", 9F, false); saveNote.ForeColor = muted; saveNote.TextAlign = ContentAlignment.MiddleRight; saveNote.Margin = new Padding(0, 0, 16, 0);
            footer.Controls.Add(saveNote, 1, 0);
            close.Dock = DockStyle.Fill;
            close.AccessibleDescription = "请求所选软件正常关闭，保留软件的保存提示。";
            footer.Controls.Add(close, 2, 0);
            layout.Controls.Add(footer, 0, 1);
            UpdateButtons();
            ResumeLayout(true);
        }

        void PaintResultCell(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            Occupant item = grid.Rows[e.RowIndex].Tag as Occupant;
            bool contrast = SystemInformation.HighContrast;
            bool selected = (e.State & DataGridViewElementStates.Selected) != 0;
            bool check = Convert.ToBoolean(grid.Rows[e.RowIndex].Cells[0].Value);
            Color background = contrast ? (selected ? SystemColors.Highlight : SystemColors.Window) : (check ? Color.FromArgb(248, 248, 248) : surface);
            Color foreground = contrast && selected ? SystemColors.HighlightText : ink;
            Color subtext = contrast ? foreground : muted;
            using (SolidBrush brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, e.CellBounds);
            float scale = e.Graphics.DpiX / 96F;
            if (e.RowIndex < grid.RowCount - 1)
                using (Pen pen = new Pen(line)) e.Graphics.DrawLine(pen, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right, e.CellBounds.Bottom - 1);
            if (e.ColumnIndex == 0)
            {
                bool disabled = item != null && item.Protected;
                check = !disabled && check;
                int size = (int)Math.Round(16 * scale);
                Rectangle box = new Rectangle(e.CellBounds.Left + (e.CellBounds.Width - size) / 2, e.CellBounds.Top + (e.CellBounds.Height - size) / 2, size, size);
                Color border = contrast ? foreground : (disabled ? Color.FromArgb(190, 190, 190) : (check ? accent : Color.FromArgb(153, 153, 153)));
                var smoothing = e.Graphics.SmoothingMode;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (System.Drawing.Drawing2D.GraphicsPath path = LightGeometry.Rounded(new RectangleF(box.Left + .5F, box.Top + .5F, box.Width - 1, box.Height - 1), 3 * scale))
                {
                    using (SolidBrush brush = new SolidBrush(check ? accent : background)) e.Graphics.FillPath(brush, path);
                    using (Pen pen = new Pen(border)) e.Graphics.DrawPath(pen, path);
                }
                if (check)
                    using (Pen pen = new Pen(contrast ? SystemColors.HighlightText : Color.White, Math.Max(1.6F, scale * 1.6F)))
                        e.Graphics.DrawLines(pen, new PointF[] { new PointF(box.Left + size * .23F, box.Top + size * .51F), new PointF(box.Left + size * .43F, box.Top + size * .71F), new PointF(box.Left + size * .78F, box.Top + size * .29F) });
                e.Graphics.SmoothingMode = smoothing;
                if (grid.Focused && grid.CurrentCell != null && grid.CurrentCell.RowIndex == e.RowIndex && grid.CurrentCell.ColumnIndex == 0)
                    ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(box, 3, 3), foreground, background);
            }
            else if (e.ColumnIndex == 1)
            {
                int symbolSize = (int)(34 * scale), symbolX = e.CellBounds.Left, symbolY = e.CellBounds.Top + (e.CellBounds.Height - symbolSize) / 2;
                var smoothing = e.Graphics.SmoothingMode; e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (System.Drawing.Drawing2D.GraphicsPath path = LightGeometry.Rounded(new RectangleF(symbolX + .5F, symbolY + .5F, symbolSize - 1, symbolSize - 1), 7 * scale))
                {
                    using (SolidBrush brush = new SolidBrush(surface)) e.Graphics.FillPath(brush, path);
                    using (Pen pen = new Pen(contrast ? foreground : Color.FromArgb(222, 222, 222))) e.Graphics.DrawPath(pen, path);
                }
                string name = item == null ? Convert.ToString(e.Value) : DisplaySoftwareName(item.Name);
                string mark = item != null && (item.Name ?? "").IndexOf("excel", StringComparison.OrdinalIgnoreCase) >= 0 ? "X" : (name.Length == 0 ? "·" : name.Substring(0, 1).ToUpperInvariant());
                using (Font iconFont = new Font(Font.FontFamily, 12F, FontStyle.Bold))
                    TextRenderer.DrawText(e.Graphics, mark, iconFont, new Rectangle(symbolX, symbolY, symbolSize, symbolSize), muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                e.Graphics.SmoothingMode = smoothing;
                int left = symbolX + symbolSize + (int)(15 * scale), width = e.CellBounds.Right - left - (int)(8 * scale);
                int nameTop = e.CellBounds.Top + (int)(16 * scale);
                using (Font nameFont = new Font(Font.FontFamily, 10F, FontStyle.Regular))
                    TextRenderer.DrawText(e.Graphics, name, nameFont, new Rectangle(left, nameTop, width, (int)(24 * scale)), foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping);
                string subtitle = item == null ? "" : FileSummary(item);
                using (Font subFont = new Font(Font.FontFamily, 9F))
                    TextRenderer.DrawText(e.Graphics, subtitle, subFont, new Rectangle(left, nameTop + (int)(25 * scale), width, (int)(20 * scale)), subtext, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping);
                if (grid.Focused && grid.CurrentCell != null && grid.CurrentCell.RowIndex == e.RowIndex && grid.CurrentCell.ColumnIndex == 1)
                    ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.CellBounds, -3, -3), foreground, background);
            }
            else if (e.ColumnIndex == 5)
            {
                float x = e.CellBounds.Left + e.CellBounds.Width / 2F, y = e.CellBounds.Top + e.CellBounds.Height / 2F;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (Pen pen = new Pen(subtext, 1.35F * scale)) e.Graphics.DrawLines(pen, new PointF[] { new PointF(x - 2 * scale, y - 4 * scale), new PointF(x + 2 * scale, y), new PointF(x - 2 * scale, y + 4 * scale) });
                if (grid.Focused && grid.CurrentCell != null && grid.CurrentCell.RowIndex == e.RowIndex && grid.CurrentCell.ColumnIndex == 5)
                    ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.CellBounds, -6, -15), foreground, background);
            }
            e.Handled = true;
        }

        static string DisplaySoftwareName(string name)
        {
            string plain = (name ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : (name ?? "");
            if (plain.Equals("EXCEL", StringComparison.OrdinalIgnoreCase)) return "Microsoft Excel";
            if (plain.Equals("Resolve", StringComparison.OrdinalIgnoreCase)) return "DaVinci Resolve";
            return plain;
        }

        static string FileSummary(Occupant item)
        {
            string first = item.Paths == null || item.Paths.Count == 0 ? "未能读取具体文件" : item.Paths[0].TrimEnd('\\');
            try { if (first.IndexOf('\\') >= 0) first = Path.GetFileName(first); } catch { }
            int count = item.Paths == null ? 0 : item.Paths.Count;
            string text = first + (count > 1 ? "  ·  等 " + count + " 个文件" : (count == 1 ? "  ·  1 个文件" : ""));
            if (item.Protected) text += "  ·  受保护";
            else if (!item.CanClose && item.CanForce) text += "  ·  无可关闭窗口";
            else if (!item.CanClose && !item.CanForce) text += "  ·  请手动处理";
            return text;
        }
        async Task Execute(Func<Task> operation)
        {
            if (busy || preview) return;
            busy = true;
            progress.Visible = true;
            UseWaitCursor = true;
            UpdateButtons();
            try { await operation(); }
            catch (Exception ex) { summary.Text = grid.Rows.Count.ToString(); SetState("操作失败", ShortReason(ex.Message) + "；请重试，详情见操作记录。", true); Log(ex.ToString()); }
            finally { busy = false; progress.Visible = false; UseWaitCursor = false; UpdateButtons(); }
        }

        async Task LoadDisks()
        {
            await Execute(async delegate
            {
                SetState("正在读取磁盘", "", false);
                diskCache = await Task.Run(() => DiskEngine.ListDisks());
                FillDiskList();
                SetState("未检查", selectedDisk == null ? "连接磁盘后，在“更多”中刷新磁盘。" : "点击“重新检查”查看结果。", false);
            });
        }

        void FillDiskList()
        {
            string keep = selectedDisk == null ? "" : selectedDisk.Identity + "|" + selectedDisk.Root;
            drives.Items.Clear();
            foreach (DiskTarget disk in diskCache.Where(x => showAll.Checked || x.CanEject)) drives.Items.Add(disk);
            int index = -1;
            for (int i = 0; i < drives.Items.Count; i++)
            {
                DiskTarget candidate = (DiskTarget)drives.Items[i];
                if (candidate.Identity + "|" + candidate.Root == keep) index = i;
            }
            if (index < 0 && drives.Items.Count > 0) index = 0;
            drives.SelectedIndex = index;
            ChangeDisk();
        }

        void ChangeDisk()
        {
            selectedDisk = drives.SelectedItem as DiskTarget;
            occupants.Clear(); grid.Rows.Clear(); summary.Text = grid.Rows.Count.ToString();
            if (selectedDisk == null) diskNote.Text = "尚未选择磁盘";
            else
            {
                diskNote.Text = selectedDisk.Model + "  ·  " + (selectedDisk.CanEject ? "外接磁盘" : "内部盘，仅检查") + (selectedDisk.RelatedRoots != null && selectedDisk.RelatedRoots.Count > 1 ? "  ·  同设备分区：" + string.Join("、", selectedDisk.RelatedRoots.ToArray()) : "");
            }
            if (!busy) SetState("未检查", selectedDisk == null ? "请选择磁盘。" : "点击“重新检查”查看结果。", false);
            UpdateButtons();
        }

        async Task<DiskTarget> ValidateDisk(DiskTarget target, bool requireEjectable = false)
        {
            var current = await Task.Run(() => DiskEngine.ListDisks());
            DiskTarget match = current.FirstOrDefault(x => x.Identity == target.Identity && x.Root.Equals(target.Root, StringComparison.OrdinalIgnoreCase) && x.DiskNumber == target.DiskNumber);
            if (match == null) throw new InvalidOperationException("磁盘已断开或设备信息发生变化。请刷新磁盘后重试。");
            if (target.RelatedRoots == null || match.RelatedRoots == null || !new HashSet<string>(target.RelatedRoots, StringComparer.OrdinalIgnoreCase).SetEquals(match.RelatedRoots)) throw new InvalidOperationException("磁盘分区或盘符已变化，请刷新后重新选择。");
            if (requireEjectable && !match.CanEject) throw new InvalidOperationException("当前设备仅支持检查，已取消关闭软件。" + match.SafetyNote);
            return match;
        }

        async Task<ScanResult> ScanIntoView(DiskTarget target)
        {
            summary.Text = grid.Rows.Count.ToString();
            SetState("正在检查", "正在查找使用此磁盘的软件…", false);
            ScanResult result = await Task.Run(() => DiskEngine.Scan(target, 40));
            Populate(result);
            return result;
        }

        async Task ScanDisk()
        {
            DiskTarget target = selectedDisk;
            if (target == null) return;
            await Execute(async delegate
            {
                target = await ValidateDisk(target);
                ScanResult result = await ScanIntoView(target);
                SetScanState(result);
            });
        }

        void Populate(ScanResult result)
        {
            occupants.Clear(); occupants.AddRange(result.Items);
            grid.Rows.Clear();
            foreach (Occupant item in VisibleOccupants(occupants).OrderBy(x => x.Protected).ThenBy(x => x.Name))
            {
                string action = item.Protected ? "受保护" : (item.CanClose ? "可正常关闭" : (item.CanForce ? "无可关闭窗口，可确认后强制结束" : "请手动处理"));
                string paths = string.Join(Environment.NewLine, item.Paths.Take(2).ToArray());
                int n = grid.Rows.Add(!item.Protected && (item.CanClose || item.CanForce), DisplaySoftwareName(item.Name), item.Pid, action, paths);
                grid.Rows[n].Tag = item;
                grid.Rows[n].Cells[0].ReadOnly = item.Protected;
                grid.Rows[n].Cells[1].ToolTipText = action + Environment.NewLine + string.Join(Environment.NewLine, item.Paths.ToArray());
                grid.Rows[n].Cells[3].ToolTipText = item.ProtectionReason;
                grid.Rows[n].Cells[5].ToolTipText = "查看 " + DisplaySoftwareName(item.Name) + " 的完整占用路径";
                grid.Rows[n].Cells[4].ToolTipText = string.Join(Environment.NewLine, item.Paths.ToArray());
                if (item.Protected) grid.Rows[n].Cells[3].Style.ForeColor = muted;
            }
            int visibleCount = VisibleOccupants(result.Items).Count();
            summary.Text = visibleCount.ToString();
            ShowSelectedDetails();
            UpdateButtons();
            Log("检查完成：" + result.Items.Count + " 个进程；" + (result.Complete ? "扫描结束" : "扫描不完整") + "。" + result.Warning);
        }

        void SetScanState(ScanResult result)
        {
            int count = VisibleOccupants(result.Items).Count();
            string title = result.Complete ? (count == 0 ? "没有需要关闭的软件" : "检查完成") : "检查未完成";
            string detail = result.Complete ? (count == 0 ? (selectedDisk != null && !selectedDisk.CanEject ? "此磁盘仅支持检查。" : (result.Items.Any(IsExplorer) ? "可在“更多”关闭相关文件夹。" : "关闭软件后，请通过 Windows 弹出硬盘。")) : "关闭前请保存工作。") : ScanFailureDetail(result);
            SetState(title, detail, !result.Complete);
        }

        static IEnumerable<Occupant> VisibleOccupants(IEnumerable<Occupant> items)
        {
            return items.Where(item => !IsExplorer(item));
        }

        static bool IsExplorer(Occupant item)
        {
            return string.Equals(item.Name, "explorer", StringComparison.OrdinalIgnoreCase) || string.Equals(item.Name, "explorer.exe", StringComparison.OrdinalIgnoreCase);
        }

        static string ShortReason(string message)
        {
            string reason = (message ?? "操作未能完成").Replace('\r', ' ').Replace('\n', ' ').Trim().TrimEnd('。', '；', '.');
            return reason.Length > 28 ? reason.Substring(0, 27) + "…" : reason;
        }

        static string ScanFailureDetail(ScanResult result)
        {
            string warning = result.Warning ?? "";
            if (warning.Contains("时间上限") || warning.Contains("秒上限")) return "检查超时，请关闭已知软件后重试。";
            if (warning.Contains("磁盘发生变化")) return "磁盘连接发生变化，请刷新后重试。";
            if (warning.Contains("权限") || warning.IndexOf("access", StringComparison.OrdinalIgnoreCase) >= 0) return "权限不足，请在“更多”中以管理员运行。";
            if (warning.Contains("无法读取的数据")) return "扫描数据读取失败，请重试；详情见操作记录。";
            return (warning.Length == 0 ? "检查未能结束" : ShortReason(warning)) + "；请重试，详情见操作记录。";
        }

        List<Occupant> CheckedItems()
        {
            List<Occupant> selected = new List<Occupant>();
            foreach (DataGridViewRow row in grid.Rows)
                if (row.Tag is Occupant && Convert.ToBoolean(row.Cells[0].Value)) selected.Add((Occupant)row.Tag);
            return selected;
        }

        static bool PrimaryCloseRequiresForce(IEnumerable<Occupant> items)
        {
            // Prefer normal closure for mixed selections. A force-only selection
            // reuses CloseSelected(true), including its explicit confirmation.
            List<Occupant> actionable = items.Where(item => !item.Protected).ToList();
            return !actionable.Any(item => item.CanClose) && actionable.Any(item => item.CanForce);
        }

        async Task CloseSelected(bool forcefully)
        {
            DiskTarget target = selectedDisk;
            List<Occupant> picks = CheckedItems().Where(x => !x.Protected && (forcefully ? x.CanForce : x.CanClose)).ToList();
            if (target == null || !target.CanEject || picks.Count == 0) return;
            await Execute(async delegate
            {
                target = await ValidateDisk(target, true);
                ScanResult current = await ScanIntoView(target);
                if (!current.Complete)
                {
                    SetState("检查未完成，尚未关闭软件", ScanFailureDetail(current), true);
                    return;
                }
                List<Occupant> live = current.Items.Where(x => picks.Any(p => p.Pid == x.Pid && p.StartTicks == x.StartTicks) && !x.Protected && (forcefully ? x.CanForce : x.CanClose)).ToList();
                if (live.Count == 0) { SetState("选中的软件已不再占用此磁盘", "列表已更新，未关闭任何软件。", false); return; }
                if (forcefully)
                {
                    string names = string.Join(Environment.NewLine, live.Select(x => "• " + x.Name + "  (PID " + x.Pid + ")").ToArray());
                    DialogResult answer = MessageBox.Show(this, "将强制结束以下程序：\r\n\r\n" + names + "\r\n\r\n未保存的文档、剪辑或正在写入的数据可能丢失。\r\n请先保存工作并停止复制。是否继续？", "确认强制结束", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                    if (answer != DialogResult.Yes) { SetState("已取消强制结束", "未结束任何程序。", false); return; }
                    target = await ValidateDisk(target, true);
                    ScanResult reconfirmed = await ScanIntoView(target);
                    if (!reconfirmed.Complete) { SetState("扫描不完整，尚未结束程序", "确认后设备或占用状态发生变化，请重新检查。", true); return; }
                    live = reconfirmed.Items.Where(x => live.Any(p => p.Pid == x.Pid && p.StartTicks == x.StartTicks) && !x.Protected && x.CanForce).ToList();
                }
                target = await ValidateDisk(target, true);
                SetState(forcefully ? "正在结束所选程序" : "正在请求所选程序正常关闭", "此操作完成后会重新检查占用。", false);
                foreach (Occupant item in live)
                {
                    ActionResult result = await Task.Run(() => DiskEngine.Close(item, forcefully));
                    Log(item.Name + " (PID " + item.Pid + ")：" + result.Message);
                }
                await Task.Delay(500);
                ScanResult after = await ScanIntoView(target);
                SetScanState(after);
            });
        }

        async Task CloseRelatedFolders()
        {
            DiskTarget target = selectedDisk;
            if (target == null || !target.CanEject) return;
            await Execute(async delegate
            {
                target = await ValidateDisk(target, true);
                SetState("正在关闭相关文件夹", "等待资源管理器响应…", false);
                string result = await ExplorerFolders.CloseForDisk(target);
                Log(result);
                bool requested = result.StartsWith("已请求关闭", StringComparison.Ordinal);
                SetState(requested ? "已请求关闭相关文件夹" : "相关文件夹未能关闭", requested ? "可再次检查占用；完整结果见操作记录。" : ShortReason(result) + "；详情见操作记录。", !requested);
            });
        }

        void SetChecked(bool value)
        {
            foreach (DataGridViewRow row in grid.Rows)
                if (row.Tag is Occupant && !((Occupant)row.Tag).Protected) row.Cells[0].Value = value;
            UpdateButtons();
        }

        void UpdateButtons()
        {
            bool ready = !busy && selectedDisk != null;
            bool removable = ready && selectedDisk.CanEject;
            scan.Enabled = ready;
            drives.Enabled = refresh.Enabled = showAll.Enabled = !busy;
            admin.Enabled = !busy && !DiskEngine.IsAdministrator();
            grid.Enabled = !busy;
            selectAll.Enabled = clear.Enabled = !busy && grid.Rows.Cast<DataGridViewRow>().Any(row => row.Tag is Occupant && !((Occupant)row.Tag).Protected && (((Occupant)row.Tag).CanClose || ((Occupant)row.Tag).CanForce));
            List<Occupant> picks = CheckedItems();
            bool primaryForce = PrimaryCloseRequiresForce(picks);
            close.Text = primaryForce ? "强制结束…" : "关闭所选";
            close.AccessibleDescription = primaryForce ? "结束前会要求确认，未保存的工作可能丢失。" : "请求所选软件正常关闭，保留软件的保存提示。";
            close.Enabled = removable && picks.Any(x => !x.Protected && (x.CanClose || x.CanForce));
            force.Enabled = removable && picks.Any(x => !x.Protected && x.CanForce);
            menuForce.Enabled = force.Enabled;
            menuFolders.Enabled = removable;
            menuSelectAll.Enabled = selectAll.Enabled;
            menuClear.Enabled = clear.Enabled;
            menuAdmin.Enabled = admin.Enabled;
            menuAdmin.Text = DiskEngine.IsAdministrator() ? "已是管理员" : "管理员运行";
            menuShowAll.Enabled = showAll.Enabled;
            summary.Text = grid.Rows.Count.ToString();
            ShowSelectedDetails();
            emptyPanel.Visible = grid.Rows.Count == 0;
            if (emptyPanel.Visible) emptyPanel.BringToFront();
            if (statePanel != null) statePanel.RowStyles[2].Height = busy ? Math.Max(2, (int)Math.Round(2 * uiScale)) : 0;
            FitResults();
        }

        void SetState(string title, string detail, bool warning)
        {
            stateTitle.Text = title;
            stateTitle.ForeColor = warning ? ink : muted;
            stateDetail.Text = detail;
            stateDetail.Visible = warning;
            if (statePanel != null) statePanel.RowStyles[1].Height = warning ? (int)Math.Round(24 * uiScale) : 0;
            emptyTitle.Text = title == "未检查" ? "尚未检查占用" : title;
            emptyNote.Text = detail;
            Log(title + "：" + detail);
        }
        void Log(string message)
        {
            journal.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
            if (journal.Count > 500) journal.RemoveAt(0);
        }

        void ShowSelectedDetails()
        {
            selectedDetail.Text = "已选择 " + CheckedItems().Count + " 个软件";
        }
        void ShowPathDetails(int rowIndex)
        {
            Occupant item = grid.Rows[rowIndex].Tag as Occupant;
            if (item != null) ShowTextWindow(item.Name + " · 完整占用路径", "PID：" + item.Pid + Environment.NewLine + "程序位置：" + item.ExePath + Environment.NewLine + item.ProtectionReason + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, item.Paths.ToArray()));
        }

        void ShowJournal() { ShowTextWindow("操作记录", string.Join(Environment.NewLine, journal.ToArray())); }

        void ShowTextWindow(string title, string content)
        {
            using (Form window = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, Size = new Size(820, 520), MinimumSize = new Size(500, 350), Font = Font, Icon = Icon })
            {
                TextBox text = new TextBox { Text = content, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, BackColor = surface, ForeColor = ink, BorderStyle = BorderStyle.None };
                window.Padding = new Padding(16);
                window.Controls.Add(text);
                window.ShowDialog(this);
            }
        }

        void Elevate(object sender, EventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory });
                Close();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                SetState("未切换到管理员权限", ex.NativeErrorCode == 1223 ? "你已取消权限请求，可以继续使用当前窗口。" : ex.Message, true);
            }
        }

        void FillPreview()
        {
            DiskTarget sample = new DiskTarget { Root = "Y:\\", Label = "ONE", Model = "TOSHIBA EXTERNAL_USB", Identity = "preview-only", DiskNumber = 1, CanEject = true, SafetyNote = "可检查占用并关闭所选软件", RelatedRoots = new List<string> { "Y:\\" } };
            diskCache.Add(sample); FillDiskList();
            ScanResult sampleResult = new ScanResult { Complete = true, Warning = "", Items = new List<Occupant> {
                new Occupant { Pid = 12840, StartTicks = 1, Name = "EXCEL", ExePath = "C:\\Program Files\\Microsoft Office\\EXCEL.EXE", CanClose = true, CanForce = true, Paths = new List<string> { "Y:\\Demo\\Reports\\sample.xlsx", "Y:\\Demo\\Reports\\~$sample.xlsx" } },
                new Occupant { Pid = 6504, StartTicks = 2, Name = "Resolve", ExePath = "C:\\Program Files\\Blackmagic Design\\DaVinci Resolve\\Resolve.exe", CanClose = true, CanForce = true, Paths = new List<string> { "Y:\\Demo\\Media\\sample.mov" } },
                new Occupant { Pid = 4280, StartTicks = 3, Name = "explorer", ExePath = "C:\\Windows\\explorer.exe", Protected = true, ProtectionReason = "资源管理器仅关闭此磁盘相关文件夹，不结束桌面进程。", Paths = new List<string> { "Y:\\项目素材" } }
            } };
            Populate(sampleResult); SetScanState(sampleResult);
        }
    }

    static class ExplorerFolders
    {
        public static Task<string> CloseForDisk(DiskTarget disk)
        {
            var completion = new TaskCompletionSource<string>();
            var cancellation = new CancellationTokenSource();
            Thread worker = new Thread(delegate()
            {
                object shell = null, windows = null;
                int closed = 0;
                try
                {
                    Type shellType = Type.GetTypeFromProgID("Shell.Application");
                    shell = Activator.CreateInstance(shellType);
                    windows = shellType.InvokeMember("Windows", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
                    int count = Convert.ToInt32(windows.GetType().InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, windows, null));
                    for (int i = count - 1; i >= 0 && !cancellation.IsCancellationRequested; i--)
                    {
                        object window = null;
                        try
                        {
                            window = windows.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, windows, new object[] { i });
                            string executable = Convert.ToString(window.GetType().InvokeMember("FullName", System.Reflection.BindingFlags.GetProperty, null, window, null));
                            if (!Path.GetFileName(executable).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                            string location = Convert.ToString(window.GetType().InvokeMember("LocationURL", System.Reflection.BindingFlags.GetProperty, null, window, null));
                            Uri uri;
                            if (!Uri.TryCreate(location, UriKind.Absolute, out uri) || !uri.IsFile || uri.IsUnc) continue;
                            string path = uri.LocalPath;
                            var roots = disk.RelatedRoots ?? new List<string> { disk.Root };
                            if (!roots.Any(root => path.Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.EndsWith("\\") ? root : root + "\\", StringComparison.OrdinalIgnoreCase))) continue;
                            if (!cancellation.IsCancellationRequested)
                            {
                                window.GetType().InvokeMember("Quit", System.Reflection.BindingFlags.InvokeMethod, null, window, null);
                                closed++;
                            }
                        }
                        catch { }
                        finally { if (window != null && Marshal.IsComObject(window)) Marshal.FinalReleaseComObject(window); }
                    }
                    completion.TrySetResult("已请求关闭 " + closed + " 个此磁盘相关的资源管理器窗口；多标签窗口可能一起关闭。 ");
                }
                catch (Exception ex) { completion.TrySetResult("相关文件夹窗口未能自动关闭：" + ex.Message); }
                finally
                {
                    if (windows != null && Marshal.IsComObject(windows)) Marshal.FinalReleaseComObject(windows);
                    if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
                }
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            return Finish(completion.Task, cancellation);
        }

        static async Task<string> Finish(Task<string> task, CancellationTokenSource cancellation)
        {
            if (await Task.WhenAny(task, Task.Delay(4000)) == task) return await task;
            cancellation.Cancel();
            return "资源管理器响应较慢，请手动关闭此磁盘的文件夹窗口后重试。";
        }
    }
}
