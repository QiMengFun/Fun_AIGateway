using FunAiGateway.Models;
using Sunny.UI;

namespace FunAiGateway.Forms
{
    partial class ChannelEditDialog : UIForm
    {
        private System.ComponentModel.IContainer components = null;
        private readonly List<ChannelConfig>? _existingChannels;
        private readonly string? _editingId;

        public ChannelConfig Channel { get; private set; } = new();

        private UILabel lblName;
        private UITextBox txtName;
        private UILabel lblType;
        private UIComboBox cmbType;
        private UILabel lblBaseUrlLabel;
        private UITextBox txtBaseUrl;
        private UILabel lblApiKey;
        private UITextBox txtApiKey;
        private UILabel lblRealModelName;
        private UITextBox txtRealModelName;
        private UILabel lblContextLength;
        private System.Windows.Forms.NumericUpDown numContextLength;
        private UILabel lblMaxOutputTokens;
        private System.Windows.Forms.NumericUpDown numMaxOutputTokens;
        private UILabel lblTimeout;
        private System.Windows.Forms.NumericUpDown numTimeout;
        private UILabel lblRetryCount;
        private System.Windows.Forms.NumericUpDown numRetryCount;
        private UICheckBox chkEnabled;
        private UILabel lblCustomHeaders;
        private UITextBox txtCustomHeaders;
        private UICheckBox chkSupportStream;
        private UICheckBox chkSupportVision;
        private UICheckBox chkSupportFunctionCalling;
        // 代理设置控件
        private UICheckBox chkProxyEnabled;
        private UILabel lblProxyType;
        private UIComboBox cmbProxyType;
        private UILabel lblProxyHost;
        private UITextBox txtProxyHost;
        private UILabel lblProxyUsername;
        private UITextBox txtProxyUsername;
        private UILabel lblProxyPassword;
        private UITextBox txtProxyPassword;
        private UIButton btnOK;
        private UIGroupBox uiGroupBox1;
        private UIButton btnCancel;

        public ChannelEditDialog(ChannelConfig? existing = null, List<ChannelConfig>? existingChannels = null)
        {
            InitializeComponent();
            btnOK.Click += BtnOK_Click;
            btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

            _existingChannels = existingChannels;

            if (existing != null)
            {
                _editingId = existing.Id;
                txtName.Text = existing.Name;
                cmbType.SelectedIndex = (int)existing.Type;
                txtBaseUrl.Text = existing.BaseUrl;
                txtApiKey.Text = existing.ApiKey;
                numTimeout.Value = existing.Timeout;
                numRetryCount.Value = existing.RetryCount;
                chkEnabled.Checked = existing.Enabled;
                txtCustomHeaders.Text = existing.CustomHeaders;
                // 代理设置
                chkProxyEnabled.Checked = existing.ProxyEnabled;
                cmbProxyType.SelectedIndex = existing.ProxyType.Equals("SOCKS5", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                txtProxyHost.Text = existing.ProxyHost;
                txtProxyUsername.Text = existing.ProxyUsername;
                txtProxyPassword.Text = existing.ProxyPassword;

                var model = existing.Models.FirstOrDefault();
                if (model != null)
                {
                    txtRealModelName.Text = model.RealModelName;
                    numContextLength.Value = model.ContextLength;
                    numMaxOutputTokens.Value = model.MaxOutputTokens;
                    chkSupportStream.Checked = model.SupportStream;
                    chkSupportVision.Checked = model.SupportVision;
                    chkSupportFunctionCalling.Checked = model.SupportFunctionCalling;
                }
                Text = "编辑渠道";
            }
            else
            {
                cmbType.SelectedIndex = 0;
                cmbProxyType.SelectedIndex = 0;
                Text = "添加渠道";
            }
        }

        private void BtnOK_Click(object? sender, EventArgs e)
        {
            var name = txtName.Text.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                this.ShowWarningDialog("请输入模型名称");
                return;
            }
            if (string.IsNullOrWhiteSpace(txtBaseUrl.Text))
            {
                this.ShowWarningDialog("请输入Base URL");
                return;
            }

            // 渠道名称唯一性校验
            if (_existingChannels != null)
            {
                var duplicate = _existingChannels.Any(c =>
                    c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && c.Id != _editingId);
                if (duplicate)
                {
                    this.ShowWarningDialog($"模型名称 '{name}' 已存在，请使用其他名称");
                    return;
                }
            }

            var realModelName = txtRealModelName.Text.Trim();
            if (string.IsNullOrEmpty(realModelName))
                realModelName = name;

            Channel = new ChannelConfig
            {
                Id = _editingId ?? Guid.NewGuid().ToString(),
                Name = name,
                Type = (ChannelType)cmbType.SelectedIndex,
                BaseUrl = txtBaseUrl.Text.Trim(),
                ApiKey = txtApiKey.Text.Trim(),
                Timeout = (int)numTimeout.Value,
                RetryCount = (int)numRetryCount.Value,
                Enabled = chkEnabled.Checked,
                CustomHeaders = txtCustomHeaders.Text.Trim(),
                // 代理设置
                ProxyEnabled = chkProxyEnabled.Checked,
                ProxyType = cmbProxyType.SelectedIndex == 1 ? "SOCKS5" : "HTTP",
                ProxyHost = txtProxyHost.Text.Trim(),
                ProxyUsername = txtProxyUsername.Text.Trim(),
                ProxyPassword = txtProxyPassword.Text.Trim(),
                Models = new List<ModelConfig>
                {
                    new()
                    {
                        ModelName = name,
                        RealModelName = realModelName,
                        ContextLength = (int)numContextLength.Value,
                        MaxOutputTokens = (int)numMaxOutputTokens.Value,
                        SupportStream = chkSupportStream.Checked,
                        SupportVision = chkSupportVision.Checked,
                        SupportFunctionCalling = chkSupportFunctionCalling.Checked
                    }
                }
            };

            DialogResult = DialogResult.OK;
            Close();
        }

        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(ChannelEditDialog));
            lblName = new UILabel();
            txtName = new UITextBox();
            lblType = new UILabel();
            cmbType = new UIComboBox();
            lblBaseUrlLabel = new UILabel();
            txtBaseUrl = new UITextBox();
            lblApiKey = new UILabel();
            txtApiKey = new UITextBox();
            lblRealModelName = new UILabel();
            txtRealModelName = new UITextBox();
            lblContextLength = new UILabel();
            numContextLength = new NumericUpDown();
            lblMaxOutputTokens = new UILabel();
            numMaxOutputTokens = new NumericUpDown();
            lblTimeout = new UILabel();
            numTimeout = new NumericUpDown();
            lblRetryCount = new UILabel();
            numRetryCount = new NumericUpDown();
            chkEnabled = new UICheckBox();
            lblCustomHeaders = new UILabel();
            txtCustomHeaders = new UITextBox();
            chkSupportStream = new UICheckBox();
            chkSupportVision = new UICheckBox();
            chkSupportFunctionCalling = new UICheckBox();
            chkProxyEnabled = new UICheckBox();
            lblProxyType = new UILabel();
            cmbProxyType = new UIComboBox();
            lblProxyHost = new UILabel();
            txtProxyHost = new UITextBox();
            lblProxyUsername = new UILabel();
            txtProxyUsername = new UITextBox();
            lblProxyPassword = new UILabel();
            txtProxyPassword = new UITextBox();
            btnOK = new UIButton();
            btnCancel = new UIButton();
            uiGroupBox1 = new UIGroupBox();
            ((System.ComponentModel.ISupportInitialize)numContextLength).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numMaxOutputTokens).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numTimeout).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numRetryCount).BeginInit();
            uiGroupBox1.SuspendLayout();
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
            lblName.Text = "模型名称:";
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
            txtName.Size = new Size(360, 29);
            txtName.TabIndex = 1;
            txtName.TextAlignment = ContentAlignment.MiddleLeft;
            txtName.Watermark = "";
            // 
            // lblType
            // 
            lblType.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblType.ForeColor = Color.FromArgb(48, 48, 48);
            lblType.Location = new Point(20, 89);
            lblType.Name = "lblType";
            lblType.Size = new Size(90, 23);
            lblType.TabIndex = 2;
            lblType.Text = "协议类型:";
            // 
            // cmbType
            // 
            cmbType.DataSource = null;
            cmbType.DropDownStyle = UIDropDownStyle.DropDownList;
            cmbType.FillColor = Color.White;
            cmbType.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            cmbType.ItemHoverColor = Color.FromArgb(155, 200, 255);
            cmbType.Items.AddRange(new object[] { "OpenAI", "Anthropic" });
            cmbType.ItemSelectForeColor = Color.FromArgb(235, 243, 255);
            cmbType.Location = new Point(120, 86);
            cmbType.Margin = new Padding(4, 5, 4, 5);
            cmbType.MinimumSize = new Size(63, 0);
            cmbType.Name = "cmbType";
            cmbType.Padding = new Padding(0, 0, 30, 2);
            cmbType.Size = new Size(360, 29);
            cmbType.SymbolSize = 24;
            cmbType.TabIndex = 3;
            cmbType.TextAlignment = ContentAlignment.MiddleLeft;
            cmbType.Watermark = "";
            // 
            // lblBaseUrlLabel
            // 
            lblBaseUrlLabel.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblBaseUrlLabel.ForeColor = Color.FromArgb(48, 48, 48);
            lblBaseUrlLabel.Location = new Point(20, 134);
            lblBaseUrlLabel.Name = "lblBaseUrlLabel";
            lblBaseUrlLabel.Size = new Size(90, 23);
            lblBaseUrlLabel.TabIndex = 4;
            lblBaseUrlLabel.Text = "Base URL:";
            // 
            // txtBaseUrl
            // 
            txtBaseUrl.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            txtBaseUrl.Location = new Point(120, 131);
            txtBaseUrl.Margin = new Padding(4, 5, 4, 5);
            txtBaseUrl.MinimumSize = new Size(1, 16);
            txtBaseUrl.Name = "txtBaseUrl";
            txtBaseUrl.Padding = new Padding(5);
            txtBaseUrl.ShowText = false;
            txtBaseUrl.Size = new Size(360, 29);
            txtBaseUrl.TabIndex = 5;
            txtBaseUrl.TextAlignment = ContentAlignment.MiddleLeft;
            txtBaseUrl.Watermark = "如 https://api.openai.com/v1";
            // 
            // lblApiKey
            // 
            lblApiKey.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblApiKey.ForeColor = Color.FromArgb(48, 48, 48);
            lblApiKey.Location = new Point(20, 179);
            lblApiKey.Name = "lblApiKey";
            lblApiKey.Size = new Size(90, 23);
            lblApiKey.TabIndex = 6;
            lblApiKey.Text = "API Key:";
            // 
            // txtApiKey
            // 
            txtApiKey.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            txtApiKey.Location = new Point(120, 176);
            txtApiKey.Margin = new Padding(4, 5, 4, 5);
            txtApiKey.MinimumSize = new Size(1, 16);
            txtApiKey.Name = "txtApiKey";
            txtApiKey.Padding = new Padding(5);
            txtApiKey.ShowText = false;
            txtApiKey.Size = new Size(360, 29);
            txtApiKey.TabIndex = 7;
            txtApiKey.TextAlignment = ContentAlignment.MiddleLeft;
            txtApiKey.Watermark = "";
            // 
            // lblRealModelName
            // 
            lblRealModelName.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblRealModelName.ForeColor = Color.FromArgb(48, 48, 48);
            lblRealModelName.Location = new Point(20, 224);
            lblRealModelName.Name = "lblRealModelName";
            lblRealModelName.Size = new Size(90, 23);
            lblRealModelName.TabIndex = 8;
            lblRealModelName.Text = "实际模型名:";
            // 
            // txtRealModelName
            // 
            txtRealModelName.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            txtRealModelName.Location = new Point(120, 221);
            txtRealModelName.Margin = new Padding(4, 5, 4, 5);
            txtRealModelName.MinimumSize = new Size(1, 16);
            txtRealModelName.Name = "txtRealModelName";
            txtRealModelName.Padding = new Padding(5);
            txtRealModelName.ShowText = false;
            txtRealModelName.Size = new Size(360, 29);
            txtRealModelName.TabIndex = 9;
            txtRealModelName.TextAlignment = ContentAlignment.MiddleLeft;
            txtRealModelName.Watermark = "";
            // 
            // lblContextLength
            // 
            lblContextLength.Font = new Font("宋体", 11F);
            lblContextLength.ForeColor = Color.FromArgb(48, 48, 48);
            lblContextLength.Location = new Point(20, 269);
            lblContextLength.Name = "lblContextLength";
            lblContextLength.Size = new Size(94, 23);
            lblContextLength.TabIndex = 10;
            lblContextLength.Text = "上下文长度:";
            // 
            // numContextLength
            // 
            numContextLength.Location = new Point(120, 266);
            numContextLength.Maximum = new decimal(new int[] { 2000000, 0, 0, 0 });
            numContextLength.Name = "numContextLength";
            numContextLength.Size = new Size(100, 26);
            numContextLength.TabIndex = 11;
            numContextLength.Value = new decimal(new int[] { 128000, 0, 0, 0 });
            // 
            // lblMaxOutputTokens
            // 
            lblMaxOutputTokens.Font = new Font("宋体", 11F);
            lblMaxOutputTokens.ForeColor = Color.FromArgb(48, 48, 48);
            lblMaxOutputTokens.Location = new Point(253, 269);
            lblMaxOutputTokens.Name = "lblMaxOutputTokens";
            lblMaxOutputTokens.Size = new Size(87, 23);
            lblMaxOutputTokens.TabIndex = 12;
            lblMaxOutputTokens.Text = "最大输出:";
            // 
            // numMaxOutputTokens
            // 
            numMaxOutputTokens.Location = new Point(346, 266);
            numMaxOutputTokens.Maximum = new decimal(new int[] { 2000000, 0, 0, 0 });
            numMaxOutputTokens.Name = "numMaxOutputTokens";
            numMaxOutputTokens.Size = new Size(100, 26);
            numMaxOutputTokens.TabIndex = 13;
            numMaxOutputTokens.Value = new decimal(new int[] { 16000, 0, 0, 0 });
            // 
            // lblTimeout
            // 
            lblTimeout.Font = new Font("宋体", 11F);
            lblTimeout.ForeColor = Color.FromArgb(48, 48, 48);
            lblTimeout.Location = new Point(22, 314);
            lblTimeout.Name = "lblTimeout";
            lblTimeout.Size = new Size(90, 23);
            lblTimeout.TabIndex = 14;
            lblTimeout.Text = "超时(秒):";
            // 
            // numTimeout
            // 
            numTimeout.Location = new Point(120, 311);
            numTimeout.Maximum = new decimal(new int[] { 600, 0, 0, 0 });
            numTimeout.Name = "numTimeout";
            numTimeout.Size = new Size(80, 26);
            numTimeout.TabIndex = 15;
            numTimeout.Value = new decimal(new int[] { 300, 0, 0, 0 });
            // 
            // lblRetryCount
            // 
            lblRetryCount.Font = new Font("宋体", 11F);
            lblRetryCount.ForeColor = Color.FromArgb(48, 48, 48);
            lblRetryCount.Location = new Point(220, 314);
            lblRetryCount.Name = "lblRetryCount";
            lblRetryCount.Size = new Size(82, 23);
            lblRetryCount.TabIndex = 16;
            lblRetryCount.Text = "重试次数:";
            // 
            // numRetryCount
            // 
            numRetryCount.Location = new Point(308, 311);
            numRetryCount.Maximum = new decimal(new int[] { 10, 0, 0, 0 });
            numRetryCount.Name = "numRetryCount";
            numRetryCount.Size = new Size(80, 26);
            numRetryCount.TabIndex = 17;
            // 
            // chkEnabled
            // 
            chkEnabled.Checked = true;
            chkEnabled.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            chkEnabled.ForeColor = Color.FromArgb(48, 48, 48);
            chkEnabled.Location = new Point(400, 311);
            chkEnabled.MinimumSize = new Size(1, 1);
            chkEnabled.Name = "chkEnabled";
            chkEnabled.Size = new Size(80, 29);
            chkEnabled.TabIndex = 18;
            chkEnabled.Text = "启用";
            // 
            // lblCustomHeaders
            // 
            lblCustomHeaders.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblCustomHeaders.ForeColor = Color.FromArgb(48, 48, 48);
            lblCustomHeaders.Location = new Point(20, 399);
            lblCustomHeaders.Name = "lblCustomHeaders";
            lblCustomHeaders.Size = new Size(90, 23);
            lblCustomHeaders.TabIndex = 22;
            lblCustomHeaders.Text = "自定义头:";
            // 
            // txtCustomHeaders
            // 
            txtCustomHeaders.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            txtCustomHeaders.Location = new Point(120, 396);
            txtCustomHeaders.Margin = new Padding(4, 5, 4, 5);
            txtCustomHeaders.MinimumSize = new Size(1, 16);
            txtCustomHeaders.Multiline = true;
            txtCustomHeaders.Name = "txtCustomHeaders";
            txtCustomHeaders.Padding = new Padding(5);
            txtCustomHeaders.ShowText = false;
            txtCustomHeaders.Size = new Size(360, 60);
            txtCustomHeaders.TabIndex = 23;
            txtCustomHeaders.TextAlignment = ContentAlignment.MiddleLeft;
            txtCustomHeaders.Watermark = "";
            // 
            // chkSupportStream
            // 
            chkSupportStream.Checked = true;
            chkSupportStream.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            chkSupportStream.ForeColor = Color.FromArgb(48, 48, 48);
            chkSupportStream.Location = new Point(20, 357);
            chkSupportStream.MinimumSize = new Size(1, 1);
            chkSupportStream.Name = "chkSupportStream";
            chkSupportStream.Size = new Size(90, 29);
            chkSupportStream.TabIndex = 19;
            chkSupportStream.Text = "支持流式";
            // 
            // chkSupportVision
            // 
            chkSupportVision.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            chkSupportVision.ForeColor = Color.FromArgb(48, 48, 48);
            chkSupportVision.Location = new Point(120, 357);
            chkSupportVision.MinimumSize = new Size(1, 1);
            chkSupportVision.Name = "chkSupportVision";
            chkSupportVision.Size = new Size(90, 29);
            chkSupportVision.TabIndex = 20;
            chkSupportVision.Text = "支持视觉";
            // 
            // chkSupportFunctionCalling
            // 
            chkSupportFunctionCalling.Checked = true;
            chkSupportFunctionCalling.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            chkSupportFunctionCalling.ForeColor = Color.FromArgb(48, 48, 48);
            chkSupportFunctionCalling.Location = new Point(220, 357);
            chkSupportFunctionCalling.MinimumSize = new Size(1, 1);
            chkSupportFunctionCalling.Name = "chkSupportFunctionCalling";
            chkSupportFunctionCalling.Size = new Size(120, 29);
            chkSupportFunctionCalling.TabIndex = 21;
            chkSupportFunctionCalling.Text = "支持函数调用";
            // 
            // chkProxyEnabled
            // 
            chkProxyEnabled.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            chkProxyEnabled.ForeColor = Color.FromArgb(48, 48, 48);
            chkProxyEnabled.Location = new Point(3, 25);
            chkProxyEnabled.MinimumSize = new Size(1, 1);
            chkProxyEnabled.Name = "chkProxyEnabled";
            chkProxyEnabled.Size = new Size(120, 29);
            chkProxyEnabled.TabIndex = 24;
            chkProxyEnabled.Text = "启用代理";
            // 
            // lblProxyType
            // 
            lblProxyType.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblProxyType.ForeColor = Color.FromArgb(48, 48, 48);
            lblProxyType.Location = new Point(3, 62);
            lblProxyType.Name = "lblProxyType";
            lblProxyType.Size = new Size(90, 23);
            lblProxyType.TabIndex = 25;
            lblProxyType.Text = "代理方法:";
            // 
            // cmbProxyType
            // 
            cmbProxyType.DataSource = null;
            cmbProxyType.DropDownStyle = UIDropDownStyle.DropDownList;
            cmbProxyType.FillColor = Color.White;
            cmbProxyType.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            cmbProxyType.ItemHoverColor = Color.FromArgb(155, 200, 255);
            cmbProxyType.Items.AddRange(new object[] { "HTTP", "SOCKS5" });
            cmbProxyType.ItemSelectForeColor = Color.FromArgb(235, 243, 255);
            cmbProxyType.Location = new Point(103, 59);
            cmbProxyType.Margin = new Padding(4, 5, 4, 5);
            cmbProxyType.MinimumSize = new Size(63, 0);
            cmbProxyType.Name = "cmbProxyType";
            cmbProxyType.Padding = new Padding(0, 0, 30, 2);
            cmbProxyType.Size = new Size(360, 29);
            cmbProxyType.SymbolSize = 24;
            cmbProxyType.TabIndex = 26;
            cmbProxyType.TextAlignment = ContentAlignment.MiddleLeft;
            cmbProxyType.Watermark = "";
            // 
            // lblProxyHost
            // 
            lblProxyHost.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblProxyHost.ForeColor = Color.FromArgb(48, 48, 48);
            lblProxyHost.Location = new Point(3, 105);
            lblProxyHost.Name = "lblProxyHost";
            lblProxyHost.Size = new Size(90, 23);
            lblProxyHost.TabIndex = 27;
            lblProxyHost.Text = "代理IP:";
            // 
            // txtProxyHost
            // 
            txtProxyHost.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            txtProxyHost.Location = new Point(103, 102);
            txtProxyHost.Margin = new Padding(4, 5, 4, 5);
            txtProxyHost.MinimumSize = new Size(1, 16);
            txtProxyHost.Name = "txtProxyHost";
            txtProxyHost.Padding = new Padding(5);
            txtProxyHost.ShowText = false;
            txtProxyHost.Size = new Size(360, 29);
            txtProxyHost.TabIndex = 28;
            txtProxyHost.TextAlignment = ContentAlignment.MiddleLeft;
            txtProxyHost.Watermark = "如 127.0.0.1:7890";
            // 
            // lblProxyUsername
            // 
            lblProxyUsername.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblProxyUsername.ForeColor = Color.FromArgb(48, 48, 48);
            lblProxyUsername.Location = new Point(3, 148);
            lblProxyUsername.Name = "lblProxyUsername";
            lblProxyUsername.Size = new Size(90, 23);
            lblProxyUsername.TabIndex = 29;
            lblProxyUsername.Text = "代理用户名:";
            // 
            // txtProxyUsername
            // 
            txtProxyUsername.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            txtProxyUsername.Location = new Point(103, 145);
            txtProxyUsername.Margin = new Padding(4, 5, 4, 5);
            txtProxyUsername.MinimumSize = new Size(1, 16);
            txtProxyUsername.Name = "txtProxyUsername";
            txtProxyUsername.Padding = new Padding(5);
            txtProxyUsername.ShowText = false;
            txtProxyUsername.Size = new Size(360, 29);
            txtProxyUsername.TabIndex = 30;
            txtProxyUsername.TextAlignment = ContentAlignment.MiddleLeft;
            txtProxyUsername.Watermark = "";
            // 
            // lblProxyPassword
            // 
            lblProxyPassword.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblProxyPassword.ForeColor = Color.FromArgb(48, 48, 48);
            lblProxyPassword.Location = new Point(3, 191);
            lblProxyPassword.Name = "lblProxyPassword";
            lblProxyPassword.Size = new Size(90, 23);
            lblProxyPassword.TabIndex = 31;
            lblProxyPassword.Text = "代理密码:";
            // 
            // txtProxyPassword
            // 
            txtProxyPassword.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            txtProxyPassword.Location = new Point(103, 188);
            txtProxyPassword.Margin = new Padding(4, 5, 4, 5);
            txtProxyPassword.MinimumSize = new Size(1, 16);
            txtProxyPassword.Name = "txtProxyPassword";
            txtProxyPassword.Padding = new Padding(5);
            txtProxyPassword.PasswordChar = '●';
            txtProxyPassword.ShowText = false;
            txtProxyPassword.Size = new Size(360, 29);
            txtProxyPassword.TabIndex = 32;
            txtProxyPassword.TextAlignment = ContentAlignment.MiddleLeft;
            txtProxyPassword.Watermark = "";
            // 
            // btnOK
            // 
            btnOK.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            btnOK.Location = new Point(133, 685);
            btnOK.MinimumSize = new Size(1, 1);
            btnOK.Name = "btnOK";
            btnOK.Size = new Size(100, 35);
            btnOK.TabIndex = 33;
            btnOK.Text = "确定";
            btnOK.TipsFont = new Font("宋体", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);
            // 
            // btnCancel
            // 
            btnCancel.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            btnCancel.Location = new Point(253, 685);
            btnCancel.MinimumSize = new Size(1, 1);
            btnCancel.Name = "btnCancel";
            btnCancel.Size = new Size(100, 35);
            btnCancel.TabIndex = 34;
            btnCancel.Text = "取消";
            btnCancel.TipsFont = new Font("宋体", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);
            // 
            // uiGroupBox1
            // 
            uiGroupBox1.Controls.Add(chkProxyEnabled);
            uiGroupBox1.Controls.Add(txtProxyPassword);
            uiGroupBox1.Controls.Add(lblProxyPassword);
            uiGroupBox1.Controls.Add(txtProxyUsername);
            uiGroupBox1.Controls.Add(lblProxyUsername);
            uiGroupBox1.Controls.Add(txtProxyHost);
            uiGroupBox1.Controls.Add(lblProxyHost);
            uiGroupBox1.Controls.Add(cmbProxyType);
            uiGroupBox1.Controls.Add(lblProxyType);
            uiGroupBox1.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            uiGroupBox1.Location = new Point(4, 457);
            uiGroupBox1.Margin = new Padding(4, 5, 4, 5);
            uiGroupBox1.MinimumSize = new Size(1, 1);
            uiGroupBox1.Name = "uiGroupBox1";
            uiGroupBox1.Padding = new Padding(0, 32, 0, 0);
            uiGroupBox1.Size = new Size(486, 225);
            uiGroupBox1.TabIndex = 35;
            uiGroupBox1.Text = "代理";
            uiGroupBox1.TextAlignment = ContentAlignment.MiddleLeft;
            // 
            // ChannelEditDialog
            // 
            AcceptButton = btnOK;
            AutoScaleMode = AutoScaleMode.None;
            CancelButton = btnCancel;
            ClientSize = new Size(494, 724);
            Controls.Add(uiGroupBox1);
            Controls.Add(lblName);
            Controls.Add(txtName);
            Controls.Add(lblType);
            Controls.Add(cmbType);
            Controls.Add(lblBaseUrlLabel);
            Controls.Add(txtBaseUrl);
            Controls.Add(lblApiKey);
            Controls.Add(txtApiKey);
            Controls.Add(lblRealModelName);
            Controls.Add(txtRealModelName);
            Controls.Add(lblContextLength);
            Controls.Add(numContextLength);
            Controls.Add(lblMaxOutputTokens);
            Controls.Add(numMaxOutputTokens);
            Controls.Add(lblTimeout);
            Controls.Add(numTimeout);
            Controls.Add(lblRetryCount);
            Controls.Add(numRetryCount);
            Controls.Add(chkEnabled);
            Controls.Add(chkSupportStream);
            Controls.Add(chkSupportVision);
            Controls.Add(chkSupportFunctionCalling);
            Controls.Add(lblCustomHeaders);
            Controls.Add(txtCustomHeaders);
            Controls.Add(btnOK);
            Controls.Add(btnCancel);
            Icon = (Icon)resources.GetObject("$this.Icon");
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "ChannelEditDialog";
            StartPosition = FormStartPosition.CenterParent;
            Text = "编辑渠道";
            ZoomScaleRect = new Rectangle(15, 15, 560, 700);
            ((System.ComponentModel.ISupportInitialize)numContextLength).EndInit();
            ((System.ComponentModel.ISupportInitialize)numMaxOutputTokens).EndInit();
            ((System.ComponentModel.ISupportInitialize)numTimeout).EndInit();
            ((System.ComponentModel.ISupportInitialize)numRetryCount).EndInit();
            uiGroupBox1.ResumeLayout(false);
            ResumeLayout(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null)) components.Dispose();
            base.Dispose(disposing);
        }
    }
}
