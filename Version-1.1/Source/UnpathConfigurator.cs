using Bindito.Core;

namespace Calloatti.Unpath
{
  [Context("Game")]
  public class UnpathConfigurator : Configurator
  {
    protected override void Configure()
    {
      Bind<UnpathService>().AsSingleton();
    }
  }
}
