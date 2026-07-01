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
        // OpenAI 协议端点控件
        private UIGroupBox grpOpenAI;
        private UILabel lblOpenAIBaseUrl;
        private UITextBox txtOpenAIBaseUrl;
        private UILabel lblOpenAIApiKey;
        private UITextBox txtOpenAIApiKey;
        // Anthropic 协议端点控件
        private UIGroupBox grpAnthropic;
        private UILabel lblAnthropicBaseUrl;
        private UITextBox txtAnthropicBaseUrl;
        private UILabel lblAnthropicApiKey;
        private UITextBox txtAnthropicApiKey;
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
                txtOpenAIBaseUrl.Text = existing.OpenAIBaseUrl;
                txtOpenAIApiKey.Text = existing.OpenAIApiKey;
                txtAnthropicBaseUrl.Text = existing.AnthropicBaseUrl;
                txtAnthropicApiKey.Text = existing.AnthropicApiKey;
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

            var openAIBaseUrl = txtOpenAIBaseUrl.Text.Trim();
            var anthropicBaseUrl = txtAnthropicBaseUrl.Text.Trim();
            // 至少需要一个协议端点的 BaseUrl 不为空
            if (string.IsNullOrWhiteSpace(openAIBaseUrl) && string.IsNullOrWhiteSpace(anthropicBaseUrl))
            {
                this.ShowWarningDialog("请至少填写一个协议端点的 Base URL");
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
                OpenAIBaseUrl = openAIBaseUrl,
                OpenAIApiKey = txtOpenAIApiKey.Text.Trim(),
                AnthropicBaseUrl = anthropicBaseUrl,
                AnthropicApiKey = txtAnthropicApiKey.Text.Trim(),
                // 旧字段 BaseUrl/ApiKey 留空（不再使用）
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
            grpOpenAI = new UIGroupBox();
            lblOpenAIBaseUrl = new UILabel();
            txtOpenAIBaseUrl = new UITextBox();
            lblOpenAIApiKey = new UILabel();
            txtOpenAIApiKey = new UITextBox();
            grpAnthropic = new UIGroupBox();
            lblAnthropicBaseUrl = new UILabel();
            txtAnthropicBaseUrl = new UITextBox();
            lblAnthropicApiKey = new UILabel();
            txtAnthropicApiKey = new UITextBox();
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
            grpOpenAI.SuspendLayout();
            grpAnthropic.SuspendLayout();
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
            // lblRealModelName
            // 
            lblRealModelName.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblRealModelName.ForeColor = Color.FromArgb(48, 48, 48);
            lblRealModelName.Location = new Point(20, 92);
            lblRealModelName.Name = "lblRealModelName";
            lblRealModelName.Size = new Size(90, 23);
            lblRealModelName.TabIndex = 2;
            lblRealModelName.Text = "实际模型名:";
            // 
            // txtRealModelName
            // 
            txtRealModelName.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            txtRealModelName.Location = new Point(120, 89);
            txtRealModelName.Margin = new Padding(4, 5, 4, 5);
            txtRealModelName.MinimumSize = new Size(1, 16);
            txtRealModelName.Name = "txtRealModelName";
            txtRealModelName.Padding = new Padding(5);
            txtRealModelName.ShowText = false;
            txtRealModelName.Size = new Size(360, 29);
            txtRealModelName.TabIndex = 3;
            txtRealModelName.TextAlignment = ContentAlignment.MiddleLeft;
            txtRealModelName.Watermark = "";
            // 
            // lblContextLength
            // 
            lblContextLength.Font = new Font("宋体", 11F);
            lblContextLength.ForeColor = Color.FromArgb(48, 48, 48);
            lblContextLength.Location = new Point(20, 137);
            lblContextLength.Name = "lblContextLength";
            lblContextLength.Size = new Size(94, 23);
            lblContextLength.TabIndex = 4;
            lblContextLength.Text = "上下文长度:";
            // 
            // numContextLength
            // 
            numContextLength.Location = new Point(120, 134);
            numContextLength.Maximum = new decimal(new int[] { 2000000, 0, 0, 0 });
            numContextLength.Name = "numContextLength";
            numContextLength.Size = new Size(100, 26);
            numContextLength.TabIndex = 5;
            numContextLength.Value = new decimal(new int[] { 128000, 0, 0, 0 });
            // 
            // lblMaxOutputTokens
            // 
            lblMaxOutputTokens.Font = new Font("宋体", 11F);
            lblMaxOutputTokens.ForeColor = Color.FromArgb(48, 48, 48);
            lblMaxOutputTokens.Location = new Point(253, 137);
            lblMaxOutputTokens.Name = "lblMaxOutputTokens";
            lblMaxOutputTokens.Size = new Size(87, 23);
            lblMaxOutputTokens.TabIndex = 6;
            lblMaxOutputTokens.Text = "最大输出:";
            // 
            // numMaxOutputTokens
            // 
            numMaxOutputTokens.Location = new Point(346, 134);
            numMaxOutputTokens.Maximum = new decimal(new int[] { 2000000, 0, 0, 0 });
            numMaxOutputTokens.Name = "numMaxOutputTokens";
            numMaxOutputTokens.Size = new Size(100, 26);
            numMaxOutputTokens.TabIndex = 7;
            numMaxOutputTokens.Value = new decimal(new int[] { 16000, 0, 0, 0 });
            // 
            // lblTimeout
            // 
            lblTimeout.Font = new Font("宋体", 11F);
            lblTimeout.ForeColor = Color.FromArgb(48, 48, 48);
            lblTimeout.Location = new Point(22, 182);
            lblTimeout.Name = "lblTimeout";
            lblTimeout.Size = new Size(90, 23);
            lblTimeout.TabIndex = 8;
            lblTimeout.Text = "超时(秒):";
            // 
            // numTimeout
            // 
            numTimeout.Location = new Point(120, 179);
            numTimeout.Maximum = new decimal(new int[] { 600, 0, 0, 0 });
            numTimeout.Name = "numTimeout";
            numTimeout.Size = new Size(80, 26);
            numTimeout.TabIndex = 9;
            numTimeout.Value = new decimal(new int[] { 300, 0, 0, 0 });
            // 
            // lblRetryCount
            // 
            lblRetryCount.Font = new Font("宋体", 11F);
            lblRetryCount.ForeColor = Color.FromArgb(48, 48, 48);
            lblRetryCount.Location = new Point(220, 182);
            lblRetryCount.Name = "lblRetryCount";
            lblRetryCount.Size = new Size(82, 23);
            lblRetryCount.TabIndex = 10;
            lblRetryCount.Text = "重试次数:";
            // 
            // numRetryCount
            // 
            numRetryCount.Location = new Point(308, 179);
            numRetryCount.Maximum = new decimal(new int[] { 10, 0, 0, 0 });
            numRetryCount.Name = "numRetryCount";
            numRetryCount.Size = new Size(80, 26);
            numRetryCount.TabIndex = 11;
            // 
            // chkEnabled
            // 
            chkEnabled.Checked = true;
            chkEnabled.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            chkEnabled.ForeColor = Color.FromArgb(48, 48, 48);
            chkEnabled.Location = new Point(400, 179);
            chkEnabled.MinimumSize = new Size(1, 1);
            chkEnabled.Name = "chkEnabled";
            chkEnabled.Size = new Size(80, 29);
            chkEnabled.TabIndex = 12;
            chkEnabled.Text = "启用";
            // 
            // lblCustomHeaders
            // 
            lblCustomHeaders.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            lblCustomHeaders.ForeColor = Color.FromArgb(48, 48, 48);
            lblCustomHeaders.Location = new Point(20, 267);
            lblCustomHeaders.Name = "lblCustomHeaders";
            lblCustomHeaders.Size = new Size(90, 23);
            lblCustomHeaders.TabIndex = 16;
            lblCustomHeaders.Text = "自定义头:";
            // 
            // txtCustomHeaders
            // 
            txtCustomHeaders.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            txtCustomHeaders.Location = new Point(120, 264);
            txtCustomHeaders.Margin = new Padding(4, 5, 4, 5);
            txtCustomHeaders.MinimumSize = new Size(1, 16);
            txtCustomHeaders.Multiline = true;
            txtCustomHeaders.Name = "txtCustomHeaders";
            txtCustomHeaders.Padding = new Padding(5);
            txtCustomHeaders.ShowText = false;
            txtCustomHeaders.Size = new Size(360, 60);
            txtCustomHeaders.TabIndex = 17;
            txtCustomHeaders.TextAlignment = ContentAlignment.MiddleLeft;
            txtCustomHeaders.Watermark = "";
            // 
            // chkSupportStream
            // 
            chkSupportStream.Checked = true;
            chkSupportStream.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            chkSupportStream.ForeColor = Color.FromArgb(48, 48, 48);
            chkSupportStream.Location = new Point(20, 225);
            chkSupportStream.MinimumSize = new Size(1, 1);
            chkSupportStream.Name = "chkSupportStream";
            chkSupportStream.Size = new Size(90, 29);
            chkSupportStream.TabIndex = 13;
            chkSupportStream.Text = "支持流式";
            // 
            // chkSupportVision
            // 
            chkSupportVision.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            chkSupportVision.ForeColor = Color.FromArgb(48, 48, 48);
            chkSupportVision.Location = new Point(120, 225);
            chkSupportVision.MinimumSize = new Size(1, 1);
            chkSupportVision.Name = "chkSupportVision";
            chkSupportVision.Size = new Size(90, 29);
            chkSupportVision.TabIndex = 14;
            chkSupportVision.Text = "支持视觉";
            // 
            // chkSupportFunctionCalling
            // 
            chkSupportFunctionCalling.Checked = true;
            chkSupportFunctionCalling.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            chkSupportFunctionCalling.ForeColor = Color.FromArgb(48, 48, 48);
            chkSupportFunctionCalling.Location = new Point(220, 225);
            chkSupportFunctionCalling.MinimumSize = new Size(1, 1);
            chkSupportFunctionCalling.Name = "chkSupportFunctionCalling";
            chkSupportFunctionCalling.Size = new Size(120, 29);
            chkSupportFunctionCalling.TabIndex = 15;
            chkSupportFunctionCalling.Text = "支持函数调用";
            // 
            // grpOpenAI
            // 
            grpOpenAI.Controls.Add(lblOpenAIBaseUrl);
            grpOpenAI.Controls.Add(txtOpenAIBaseUrl);
            grpOpenAI.Controls.Add(lblOpenAIApiKey);
            grpOpenAI.Controls.Add(txtOpenAIApiKey);
            grpOpenAI.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            grpOpenAI.Location = new Point(4, 332);
            grpOpenAI.Margin = new Padding(4, 5, 4, 5);
            grpOpenAI.MinimumSize = new Size(1, 1);
            grpOpenAI.Name = "grpOpenAI";
            grpOpenAI.Padding = new Padding(0, 32, 0, 0);
            grpOpenAI.Size = new Size(486, 110);
            grpOpenAI.TabIndex = 18;
            grpOpenAI.Text = "OpenAI 协议";
            grpOpenAI.TextAlignment = ContentAlignment.MiddleLeft;
            // 
            // lblOpenAIBaseUrl
            // 
            lblOpenAIBaseUrl.Font = new Font("宋体", 10F);
            lblOpenAIBaseUrl.ForeColor = Color.FromArgb(48, 48, 48);
            lblOpenAIBaseUrl.Location = new Point(8, 38);
            lblOpenAIBaseUrl.Name = "lblOpenAIBaseUrl";
            lblOpenAIBaseUrl.Size = new Size(70, 23);
            lblOpenAIBaseUrl.TabIndex = 0;
            lblOpenAIBaseUrl.Text = "Base URL:";
            // 
            // txtOpenAIBaseUrl
            // 
            txtOpenAIBaseUrl.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            txtOpenAIBaseUrl.Location = new Point(88, 35);
            txtOpenAIBaseUrl.Margin = new Padding(4, 5, 4, 5);
            txtOpenAIBaseUrl.MinimumSize = new Size(1, 16);
            txtOpenAIBaseUrl.Name = "txtOpenAIBaseUrl";
            txtOpenAIBaseUrl.Padding = new Padding(5);
            txtOpenAIBaseUrl.ShowText = false;
            txtOpenAIBaseUrl.Size = new Size(380, 29);
            txtOpenAIBaseUrl.TabIndex = 1;
            txtOpenAIBaseUrl.TextAlignment = ContentAlignment.MiddleLeft;
            txtOpenAIBaseUrl.Watermark = "如 https://api.openai.com/v1";
            // 
            // lblOpenAIApiKey
            // 
            lblOpenAIApiKey.Font = new Font("宋体", 10F);
            lblOpenAIApiKey.ForeColor = Color.FromArgb(48, 48, 48);
            lblOpenAIApiKey.Location = new Point(8, 73);
            lblOpenAIApiKey.Name = "lblOpenAIApiKey";
            lblOpenAIApiKey.Size = new Size(70, 23);
            lblOpenAIApiKey.TabIndex = 2;
            lblOpenAIApiKey.Text = "API Key:";
            // 
            // txtOpenAIApiKey
            // 
            txtOpenAIApiKey.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            txtOpenAIApiKey.Location = new Point(88, 70);
            txtOpenAIApiKey.Margin = new Padding(4, 5, 4, 5);
            txtOpenAIApiKey.MinimumSize = new Size(1, 16);
            txtOpenAIApiKey.Name = "txtOpenAIApiKey";
            txtOpenAIApiKey.Padding = new Padding(5);
            txtOpenAIApiKey.ShowText = false;
            txtOpenAIApiKey.Size = new Size(380, 29);
            txtOpenAIApiKey.TabIndex = 3;
            txtOpenAIApiKey.TextAlignment = ContentAlignment.MiddleLeft;
            txtOpenAIApiKey.Watermark = "";
            // 
            // grpAnthropic
            // 
            grpAnthropic.Controls.Add(lblAnthropicBaseUrl);
            grpAnthropic.Controls.Add(txtAnthropicBaseUrl);
            grpAnthropic.Controls.Add(lblAnthropicApiKey);
            grpAnthropic.Controls.Add(txtAnthropicApiKey);
            grpAnthropic.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            grpAnthropic.Location = new Point(4, 447);
            grpAnthropic.Margin = new Padding(4, 5, 4, 5);
            grpAnthropic.MinimumSize = new Size(1, 1);
            grpAnthropic.Name = "grpAnthropic";
            grpAnthropic.Padding = new Padding(0, 32, 0, 0);
            grpAnthropic.Size = new Size(486, 110);
            grpAnthropic.TabIndex = 19;
            grpAnthropic.Text = "Anthropic 协议";
            grpAnthropic.TextAlignment = ContentAlignment.MiddleLeft;
            // 
            // lblAnthropicBaseUrl
            // 
            lblAnthropicBaseUrl.Font = new Font("宋体", 10F);
            lblAnthropicBaseUrl.ForeColor = Color.FromArgb(48, 48, 48);
            lblAnthropicBaseUrl.Location = new Point(8, 38);
            lblAnthropicBaseUrl.Name = "lblAnthropicBaseUrl";
            lblAnthropicBaseUrl.Size = new Size(70, 23);
            lblAnthropicBaseUrl.TabIndex = 0;
            lblAnthropicBaseUrl.Text = "Base URL:";
            // 
            // txtAnthropicBaseUrl
            // 
            txtAnthropicBaseUrl.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            txtAnthropicBaseUrl.Location = new Point(88, 35);
            txtAnthropicBaseUrl.Margin = new Padding(4, 5, 4, 5);
            txtAnthropicBaseUrl.MinimumSize = new Size(1, 16);
            txtAnthropicBaseUrl.Name = "txtAnthropicBaseUrl";
            txtAnthropicBaseUrl.Padding = new Padding(5);
            txtAnthropicBaseUrl.ShowText = false;
            txtAnthropicBaseUrl.Size = new Size(380, 29);
            txtAnthropicBaseUrl.TabIndex = 1;
            txtAnthropicBaseUrl.TextAlignment = ContentAlignment.MiddleLeft;
            txtAnthropicBaseUrl.Watermark = "如 https://api.anthropic.com";
            // 
            // lblAnthropicApiKey
            // 
            lblAnthropicApiKey.Font = new Font("宋体", 10F);
            lblAnthropicApiKey.ForeColor = Color.FromArgb(48, 48, 48);
            lblAnthropicApiKey.Location = new Point(8, 73);
            lblAnthropicApiKey.Name = "lblAnthropicApiKey";
            lblAnthropicApiKey.Size = new Size(70, 23);
            lblAnthropicApiKey.TabIndex = 2;
            lblAnthropicApiKey.Text = "API Key:";
            // 
            // txtAnthropicApiKey
            // 
            txtAnthropicApiKey.Font = new Font("宋体", 12F, FontStyle.Regular, GraphicsUnit.Point, 134);
            txtAnthropicApiKey.Location = new Point(88, 70);
            txtAnthropicApiKey.Margin = new Padding(4, 5, 4, 5);
            txtAnthropicApiKey.MinimumSize = new Size(1, 16);
            txtAnthropicApiKey.Name = "txtAnthropicApiKey";
            txtAnthropicApiKey.Padding = new Padding(5);
            txtAnthropicApiKey.ShowText = false;
            txtAnthropicApiKey.Size = new Size(380, 29);
            txtAnthropicApiKey.TabIndex = 3;
            txtAnthropicApiKey.TextAlignment = ContentAlignment.MiddleLeft;
            txtAnthropicApiKey.Watermark = "";
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
            btnOK.Location = new Point(133, 805);
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
            btnCancel.Location = new Point(253, 805);
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
            uiGroupBox1.Location = new Point(4, 565);
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
            ClientSize = new Size(494, 852);
            Controls.Add(uiGroupBox1);
            Controls.Add(grpOpenAI);
            Controls.Add(grpAnthropic);
            Controls.Add(lblName);
            Controls.Add(txtName);
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
            grpOpenAI.ResumeLayout(false);
            grpAnthropic.ResumeLayout(false);
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
