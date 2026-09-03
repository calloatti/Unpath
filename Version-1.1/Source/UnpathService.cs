using Timberborn.CoreUI;
using Timberborn.Modding;
using Timberborn.SingletonSystem;
using Timberborn.UILayoutSystem;
using UnityEngine;

namespace Calloatti.Unpath
{
  public class UnpathService : ILoadableSingleton
  {
    private readonly EventBus _eventBus;
    public UnpathService(EventBus eventBus) => _eventBus = eventBus;
    public void Load() => _eventBus.Register(this);

    [OnEvent]
    public void OnShowPrimaryUI(ShowPrimaryUIEvent @event)
    {
      PlacementContext.IsFunctional = true;
      Debug.Log("[UNPATH] UI loaded, mod functionality enabled.");
    }
  }
}
