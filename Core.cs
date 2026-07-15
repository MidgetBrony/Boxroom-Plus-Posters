using MelonLoader;

[assembly: MelonInfo(typeof(Boxroom_Plus.Core), "Boxroom-Plus-Posters", "1.0.0", "MidgetBrony", null)]
[assembly: MelonGame("NestedLoop", "BOXROOM")]

namespace Boxroom_Plus
{
    public class Core : MelonMod
    {
        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("[Boxroom Plus Posters] Initialized.");
        }
    }
}