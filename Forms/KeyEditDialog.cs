using FunAiGateway.Models;
using Sunny.UI;

namespace FunAiGateway.Forms
{
    // 密钥编辑对话框：编辑Key名称、启用状态、允许访问的模型列表
    public partial class KeyEditDialog : UIForm
    {
        private System.ComponentModel.IContainer components = null;
        private readonly List<string> _allModels;

        public ApiKeyConfig ApiKey { get; private set; }

        private UILabel lblName;
        private UITextBox txtName;
        private UILabel lblKey;
        private UITextBox txtKey;
        private UICheckBox chkEnabled;
        private UILabel lblRemaining;
        private NumericUpDown numRemaining;
        private UILabel lblExpires;
        private DateTimePicker dtpExpires;
        private UICheckBox chkNeverExpires;
        private UILabel lblModels;
        private System.Windows.Forms.CheckedListBox clbModels;
        private UIButton btnSelectAll;
        private UIButton btnClearAll;
        private UIButton btnOK;
        private UICheckBox chkUnlimitedCalls;
        private UIButton btnCancel;

        public KeyEditDialog(ApiKeyConfig apiKey, List<string> allModels)
        {
            InitializeComponent();
            _allModels = allModels ?? new();
            ApiKey = apiKey;
            LoadData();
            btnOK.Click += BtnOK_Click;
            btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            btnSelectAll.Click += (_, _) => { for (int i = 0; i < clbModels.Items.Count; i++) clbModels.SetItemChecked(i, true); };
            btnClearAll.Click += (_, _) => { for (int i = 0; i < clbModels.Items.Count; i++) clbModels.SetItemChecked(i, false); };
            chkNeverExpires.ValueChanged += (_, _) => { dtpExpires.Enabled = !chkNeverExpires.Checked; };
            // 无限调用：勾选后禁用次数输入框
            chkUnlimitedCalls.ValueChanged += (_, _) => { numRemaining.Enabled = !chkUnlimitedCalls.Checked; };
        }

        private void LoadData()
        {
            txtName.Text = ApiKey.Name;
            txtKey.Text = ApiKey.Key;
            chkEnabled.Checked = ApiKey.Enabled;
            // 剩余次数：0 表示无限调用
            if (ApiKey.RemainingCalls == 0)
            {
                chkUnlimitedCalls.Checked = true;
                numRemaining.Value = 0;
                numRemaining.Enabled = false;
            }
            else
            {
                chkUnlimitedCalls.Checked = false;
                numRemaining.Value = ApiKey.RemainingCalls;
                numRemaining.Enabled = true;
            }
            // 到期时间：null 表示永不过期
            if (ApiKey.ExpiresAt.HasValue)
            {
                chkNeverExpires.Checked = false;
                dtpExpires.Value = ApiKey.ExpiresAt.Value;
                dtpExpires.Enabled = true;
            }
            else
            {
                chkNeverExpires.Checked = true;
                dtpExpires.Value = DateTime.Now.AddDays(30);
                dtpExpires.Enabled = false;
            }

            clbModels.Items.Clear();
            // 空列表表示允许全部，这里显示"全部"选项
            clbModels.Items.Add("全部模型（不勾选任何具体模型即代表允许全部）", false);
            foreach (var m in _allModels)
            {
                var checked_ = ApiKey.AllowedModels != null && ApiKey.AllowedModels.Contains(m);
                clbModels.Items.Add(m, checked_);
            }
        }

        private void BtnOK_Click(object? sender, EventArgs e)
        {
            var name = txtName.Text.Trim();
            var key = txtKey.Text.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                this.ShowWarningDialog("密钥不能为空");
                return;
            }

            // 收集勾选的模型（跳过第一项"全部模型"提示项）
            var allowed = new List<string>();
            for (int i = 1; i < clbModels.Items.Count; i++)
            {
                if (clbModels.GetItemChecked(i))
                {
                    allowed.Add(clbModels.Items[i].ToString() ?? "");
                }
            }

            ApiKey.Name = name;
            ApiKey.Key = key;
            ApiKey.Enabled = chkEnabled.Checked;
            // 无限调用勾选时 RemainingCalls=0，否则取输入值
            ApiKey.RemainingCalls = chkUnlimitedCalls.Checked ? 0 : (int)numRemaining.Value;
            ApiKey.ExpiresAt = chkNeverExpires.Checked ? null : dtpExpires.Value;
            ApiKey.AllowedModels = allowed;

            DialogResult = DialogResult.OK;
            Close();
        }

        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(KeyEditDialog));
            lblName = new UILabel();
            txtName = new UITextBox();
            lblKey = new UILabel();
            txtKey = new UITextBox();
            chkEnabled = new UICheckBox();
            lblRemaining = new UILabel();
            numRemaining = new NumericUpDown();
            lblExpires = new UILabel();
            dtpExpires = new DateTimePicker();
            chkNeverExpires = new UICheckBox();
            lblModels = new UILabel();
            clbModels = new CheckedListBox();
            btnSelectAll = new UIButton();
            btnClearAll = new UIButton();
            btnOK = new UIButton();
            btnCancel = new UIButton();
            chkUnlimitedCalls = new UICheckBox();
            ((System.ComponentModel.ISupportInitialize)numRemaining).BeginInit();
            SuspendLayout();
            // 
            // lblName
            // 
            lblName.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblName.ForeColor = Color.FromArgb(48, 48, 48);
            lblName.Location = new Point(20, 47);
            lblName.Name = "lblName";
            lblName.Size = new Size(90, 23);
            lblName.TabIndex = 0;
            lblName.Text = "名称备注:";
            // 
            // txtName
            // 
            txtName.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            txtName.Location = new Point(120, 47);
            txtName.Margin = new Padding(4, 5, 4, 5);
            txtName.MinimumSize = new Size(1, 16);
            txtName.Name = "txtName";
            txtName.Padding = new Padding(5);
            txtName.ShowText = false;
            txtName.Size = new Size(440, 29);
            txtName.TabIndex = 1;
            txtName.TextAlignment = ContentAlignment.MiddleLeft;
            txtName.Watermark = "可选备注";
            // 
            // lblKey
            // 
            lblKey.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblKey.ForeColor = Color.FromArgb(48, 48, 48);
            lblKey.Location = new Point(20, 86);
            lblKey.Name = "lblKey";
            lblKey.Size = new Size(90, 23);
            lblKey.TabIndex = 2;
            lblKey.Text = "API Key:";
            // 
            // txtKey
            // 
            txtKey.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            txtKey.Location = new Point(120, 83);
            txtKey.Margin = new Padding(4, 5, 4, 5);
            txtKey.MinimumSize = new Size(1, 16);
            txtKey.Name = "txtKey";
            txtKey.Padding = new Padding(5);
            txtKey.ShowText = false;
            txtKey.Size = new Size(440, 29);
            txtKey.TabIndex = 3;
            txtKey.TextAlignment = ContentAlignment.MiddleLeft;
            txtKey.Watermark = "fk-xxxx";
            // 
            // chkEnabled
            // 
            chkEnabled.Checked = true;
            chkEnabled.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            chkEnabled.ForeColor = Color.FromArgb(48, 48, 48);
            chkEnabled.Location = new Point(20, 121);
            chkEnabled.MinimumSize = new Size(1, 1);
            chkEnabled.Name = "chkEnabled";
            chkEnabled.Size = new Size(120, 29);
            chkEnabled.TabIndex = 4;
            chkEnabled.Text = "启用";
            // 
            // lblRemaining
            // 
            lblRemaining.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblRemaining.ForeColor = Color.FromArgb(48, 48, 48);
            lblRemaining.Location = new Point(141, 127);
            lblRemaining.Name = "lblRemaining";
            lblRemaining.Size = new Size(152, 23);
            lblRemaining.TabIndex = 5;
            lblRemaining.Text = "剩余可调用次数:";
            // 
            // numRemaining
            // 
            numRemaining.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            numRemaining.Location = new Point(307, 124);
            numRemaining.Maximum = new decimal(new int[] { 100000000, 0, 0, 0 });
            numRemaining.Name = "numRemaining";
            numRemaining.Size = new Size(133, 26);
            numRemaining.TabIndex = 6;
            // 
            // lblExpires
            // 
            lblExpires.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblExpires.ForeColor = Color.FromArgb(48, 48, 48);
            lblExpires.Location = new Point(143, 158);
            lblExpires.Name = "lblExpires";
            lblExpires.Size = new Size(90, 23);
            lblExpires.TabIndex = 7;
            lblExpires.Text = "到期时间:";
            // 
            // dtpExpires
            // 
            dtpExpires.CustomFormat = "yyyy-MM-dd HH:mm";
            dtpExpires.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            dtpExpires.Format = DateTimePickerFormat.Custom;
            dtpExpires.Location = new Point(240, 155);
            dtpExpires.Name = "dtpExpires";
            dtpExpires.Size = new Size(200, 26);
            dtpExpires.TabIndex = 8;
            dtpExpires.Value = new DateTime(2026, 7, 30, 19, 54, 1, 109);
            // 
            // chkNeverExpires
            // 
            chkNeverExpires.Checked = true;
            chkNeverExpires.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            chkNeverExpires.ForeColor = Color.FromArgb(48, 48, 48);
            chkNeverExpires.Location = new Point(450, 158);
            chkNeverExpires.MinimumSize = new Size(1, 1);
            chkNeverExpires.Name = "chkNeverExpires";
            chkNeverExpires.Size = new Size(110, 29);
            chkNeverExpires.TabIndex = 9;
            chkNeverExpires.Text = "永不过期";
            // 
            // lblModels
            // 
            lblModels.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblModels.ForeColor = Color.FromArgb(48, 48, 48);
            lblModels.Location = new Point(20, 196);
            lblModels.Name = "lblModels";
            lblModels.Size = new Size(540, 23);
            lblModels.TabIndex = 10;
            lblModels.Text = "允许访问的模型（不勾选任何具体模型即代表允许全部）:";
            // 
            // clbModels
            // 
            clbModels.CheckOnClick = true;
            clbModels.Font = new Font("宋体", 11F, FontStyle.Regular, GraphicsUnit.Point, 134);
            clbModels.Location = new Point(20, 221);
            clbModels.Name = "clbModels";
            clbModels.Size = new Size(540, 232);
            clbModels.TabIndex = 11;
            // 
            // btnSelectAll
            // 
            btnSelectAll.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            btnSelectAll.Location = new Point(20, 460);
            btnSelectAll.MinimumSize = new Size(1, 1);
            btnSelectAll.Name = "btnSelectAll";
            btnSelectAll.Size = new Size(100, 32);
            btnSelectAll.TabIndex = 12;
            btnSelectAll.Text = "全选";
            btnSelectAll.TipsFont = new Font("宋体", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);
            // 
            // btnClearAll
            // 
            btnClearAll.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            btnClearAll.Location = new Point(130, 460);
            btnClearAll.MinimumSize = new Size(1, 1);
            btnClearAll.Name = "btnClearAll";
            btnClearAll.Size = new Size(100, 32);
            btnClearAll.TabIndex = 13;
            btnClearAll.Text = "清空";
            btnClearAll.TipsFont = new Font("宋体", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);
            // 
            // btnOK
            // 
            btnOK.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            btnOK.Location = new Point(360, 460);
            btnOK.MinimumSize = new Size(1, 1);
            btnOK.Name = "btnOK";
            btnOK.Size = new Size(90, 32);
            btnOK.TabIndex = 14;
            btnOK.Text = "确定";
            btnOK.TipsFont = new Font("宋体", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);
            // 
            // btnCancel
            // 
            btnCancel.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            btnCancel.Location = new Point(460, 460);
            btnCancel.MinimumSize = new Size(1, 1);
            btnCancel.Name = "btnCancel";
            btnCancel.Size = new Size(90, 32);
            btnCancel.TabIndex = 15;
            btnCancel.Text = "取消";
            btnCancel.TipsFont = new Font("宋体", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);
            // 
            // chkUnlimitedCalls
            // 
            chkUnlimitedCalls.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            chkUnlimitedCalls.ForeColor = Color.FromArgb(48, 48, 48);
            chkUnlimitedCalls.Location = new Point(450, 120);
            chkUnlimitedCalls.MinimumSize = new Size(1, 1);
            chkUnlimitedCalls.Name = "chkUnlimitedCalls";
            chkUnlimitedCalls.Size = new Size(110, 29);
            chkUnlimitedCalls.TabIndex = 16;
            chkUnlimitedCalls.Text = "无限调用";
            // 
            // KeyEditDialog
            // 
            AcceptButton = btnOK;
            AutoScaleMode = AutoScaleMode.None;
            CancelButton = btnCancel;
            ClientSize = new Size(580, 510);
            Controls.Add(chkUnlimitedCalls);
            Controls.Add(lblName);
            Controls.Add(txtName);
            Controls.Add(lblKey);
            Controls.Add(txtKey);
            Controls.Add(chkEnabled);
            Controls.Add(lblRemaining);
            Controls.Add(numRemaining);
            Controls.Add(lblExpires);
            Controls.Add(dtpExpires);
            Controls.Add(chkNeverExpires);
            Controls.Add(lblModels);
            Controls.Add(clbModels);
            Controls.Add(btnSelectAll);
            Controls.Add(btnClearAll);
            Controls.Add(btnOK);
            Controls.Add(btnCancel);
            Icon = (Icon)resources.GetObject("$this.Icon");
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "KeyEditDialog";
            StartPosition = FormStartPosition.CenterParent;
            Text = "编辑密钥";
            ZoomScaleRect = new Rectangle(15, 15, 580, 510);
            ((System.ComponentModel.ISupportInitialize)numRemaining).EndInit();
            ResumeLayout(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }
    }
}
