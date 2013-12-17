using System.Collections.Generic;
using NMSD.Cronus.Core.Eventing;

namespace NMSD.Cronus.Core.Cqrs
{
    public interface IAggregateRoot : IAggregateRootStateManager
    {
        void Apply(IEvent @event);
        List<IEvent> UncommittedEvents { get; set; }
    }
}