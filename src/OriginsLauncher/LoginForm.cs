using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OriginsLauncher
{
    /// <summary>
    ///     Asks the player for their account and password and gets a login token from the site before the game
    ///     starts. The 6609 client sends that token in place of the password (docs\migracao-6609.md).
    /// </summary>
    internal sealed class LoginForm : Form
    {
        private readonly Config m_config;
        private readonly TextBox m_account;
        private readonly TextBox m_password;
        private readonly Label m_error;
        private readonly Button m_login;

        public IssuedToken Token { get; private set; }
        public string AccountName { get; private set; }
        public string Secret { get; private set; }

        public LoginForm(Config config, string account = null)
        {
            m_config = config;
            Text = L.S.LoginTitle;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 10f);
            ClientSize = new Size(360, 212);

            var title = new Label
            {
                Text = L.S.LoginTitle, ForeColor = Theme.Gold, AutoSize = false,
                Font = new Font("Segoe UI", 13f, FontStyle.Bold), Location = new Point(24, 16), Size = new Size(312, 28)
            };

            var accLabel = new Label {Text = L.S.Account, AutoSize = true, Location = new Point(24, 54), ForeColor = Theme.Muted};
            m_account = new TextBox
            {
                Location = new Point(24, 74), Size = new Size(312, 24), Text = account ?? "",
                BackColor = Color.FromArgb(24, 26, 33), ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, MaxLength = 16
            };

            var pwLabel = new Label {Text = L.S.Password, AutoSize = true, Location = new Point(24, 102), ForeColor = Theme.Muted};
            m_password = new TextBox
            {
                Location = new Point(24, 122), Size = new Size(312, 24),
                BackColor = Color.FromArgb(24, 26, 33), ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle,
                UseSystemPasswordChar = true, MaxLength = 32
            };

            m_error = new Label
            {
                AutoSize = false, Location = new Point(24, 150), Size = new Size(312, 18),
                ForeColor = Theme.Error, Font = new Font("Segoe UI", 8.5f)
            };

            m_login = new Button
            {
                Text = L.S.LogIn, Location = new Point(236, 174), Size = new Size(100, 28),
                FlatStyle = FlatStyle.Flat, BackColor = Theme.Gold, ForeColor = Color.FromArgb(26, 18, 6)
            };
            m_login.FlatAppearance.BorderColor = Theme.GoldDark;
            m_login.Click += async (s, e) => await SubmitAsync();

            AcceptButton = m_login;
            Controls.AddRange(new Control[] {title, accLabel, m_account, pwLabel, m_password, m_error, m_login});
            ActiveControl = m_account.Text.Length == 0 ? m_account : m_password;
        }

        private async Task SubmitAsync()
        {
            string account = m_account.Text.Trim();
            string password = m_password.Text;
            if (account.Length == 0 || password.Length == 0)
            {
                m_error.Text = L.S.LoginInvalid;
                return;
            }

            m_error.Text = "";
            SetBusy(true);
            try
            {
                // relogin: the launcher keeps this session's tokens ready for the game's login screen (ReloginKeeper)
                Token = await LoginClient.IssueAsync(m_config, account, password, true);
                AccountName = account;
                Secret = password;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (LoginException ex)
            {
                m_error.Text = ex.Message;
                SetBusy(false);
                m_password.SelectAll();
                m_password.Focus();
            }
        }

        private void SetBusy(bool busy)
        {
            m_login.Enabled = !busy;
            m_account.Enabled = !busy;
            m_password.Enabled = !busy;
            m_login.Text = busy ? L.S.Starting : L.S.LogIn;
            UseWaitCursor = busy;
        }
    }
}
