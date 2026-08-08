using LiveSplit.Model;
using LiveSplit.Sly4BHLoadRemover;
using LiveSplit.UI.Components;
using System;

[assembly: ComponentFactory(typeof(Sly4BHLoadRemovalFactory))]

namespace LiveSplit.Sly4BHLoadRemover
{
    public class Sly4BHLoadRemovalFactory : IComponentFactory
    {
        public string ComponentName
        {
            get { return "Load Remover (Sly4/Hackpack)"; }
        }

        public ComponentCategory Category
        {
            get { return ComponentCategory.Control; }
        }

        public string Description
        {
            get { return "Automatically detects and removes loads (GameTime) for Sly Cooper: Thieves in Time."; }
        }

        public IComponent Create(LiveSplitState state)
        {
            return new Sly4BHLoadRemovalComponent(state);
        }

        public string UpdateName
        {
            get { return ComponentName; }
        }
        public string UpdateURL => "https://raw.githubusercontent.com/knuutti/LiveSplit.Sly4BHLoadRemoval/master/";
        public string XMLURL => UpdateURL + "update.LiveSplit.Sly4BHLoadRemoval.xml";


        public Version Version
        {
            get { return Version.Parse("1.0"); }
        }
    }
}
