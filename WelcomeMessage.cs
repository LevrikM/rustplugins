namespace Oxide.Plugins
{
    [Info("WelcomeMessage", "LEVRO", "1.0.3")]
    [Description("Sends a private welcome message to a player on connect.")]
    public class WelcomeMessage : RustPlugin
    {
        private WelcomeConfig _cfg;

        private class WelcomeConfig
        {
            public string ServerName   { get; set; } = "Rustalgia";
            public string DiscordUrl   { get; set; } = "discord.gg/rustalgia";
            public float  DelaySeconds { get; set; } = 3f;
        }

        protected override void LoadDefaultConfig()
        {
            _cfg = new WelcomeConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _cfg = Config.ReadObject<WelcomeConfig>();
            }
            catch
            {
                _cfg = new WelcomeConfig();
                SaveConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_cfg, true);

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;

            timer.Once(_cfg.DelaySeconds, () =>
            {
                if (player == null || !player.IsConnected) return;

                player.ChatMessage(
                    $"<color=#A855F7>--------------------------</color>\n" +
                    $"<color=#A855F7><b>[ {_cfg.ServerName} ]</b></color>\n" +
                    $"Добро пожаловать на <color=#A855F7><b>{_cfg.ServerName}</b></color>!\n" +
                    $"Discord: <color=#7B61FF><b>{_cfg.DiscordUrl}</b></color>\n" +
                    $"Приятной игры — соблюдайте правила!\n" +
                    $"<color=#A855F7>--------------------------</color>"
                );
            });
        }
    }
}
