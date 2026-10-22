// Imported from an eventmodelers.ai board by `bobcat import-event-model`.
//
// This is ordinary C# and nothing regenerates it. The segmentation an import performs is a
// set of reported guesses, so a wrong guess is a one-line edit here rather than a re-import.

using JasperFx.Events.EventModeling;
using K9Crush;

namespace K9Crush.TheSwiper;

public class TheSwiperModel : EventModelDefinition
{
    public override void Configure(EventModelBuilder model)
    {
        model.InChapter("TheSwiper");
        model.Aggregate<Dog>();
        model.Aggregate<MutualMatch>();

        // From chapter 'TheSwiper', actor 'Member'.
        // ⚠ inferred: decides against Dog — its examples give Dog events before it appends; 'Dog' is the subject 2 of its 2 events share.
        model.Command<SwipeOnDog>()
            .TriggeredBy("Discovery Feed", TriggerKind.Human)
            .Against<Dog>()
            .Emits<DogLiked>()
            .Emits<DogPassed>()
            .LinksToSpecification("SwipeOnDog/a like is recorded");

        // From chapter 'TheSwiper', actor 'System'.
        // ⚠ no example says whether it starts the MutualMatch stream, so it is assumed to append to one.
        // ⚠ inferred: decides against MutualMatch — its examples give MutualMatch events before it appends; named for the subject of MutualMatchDetected, its only event.
        model.Automation("DetectMutualMatch")
            .InDomain("Discovery")
            .TriggeredBy("Dog Liked", TriggerKind.MessageHandler)
            .Command<DetectMutualMatch>()
            .Against<MutualMatch>()
            .Emits<MutualMatchDetected>();

        // From chapter 'TheSwiper', actor 'Member'.
        model.View<MatchList>()
            .On<DogLiked>()
            .On<DogPassed>()
            .On<MutualMatchDetected>()
            .LinksToSpecification("MatchList/matches show");
    }
}
