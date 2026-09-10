%copyright%namespace %namespace%

open FlaxEngine

/// <summary>
/// %class% Actor.
/// </summary>
type %class%() =
    inherit Actor()

    override this.OnBeginPlay() =
        base.OnBeginPlay()
        // Here you can add code that needs to be called when Actor added to the game. This is called during edit time as well.

    override this.OnEndPlay() =
        base.OnEndPlay()
        // Here you can add code that needs to be called when Actor removed to the game. This is called during edit time as well.

    override this.OnEnable() =
        base.OnEnable()
        // Here you can add code that needs to be called when Actor is enabled (eg. register for events). This is called during edit time as well.

    override this.OnDisable() =
        base.OnDisable()
        // Here you can add code that needs to be called when Actor is disabled (eg. unregister from events). This is called during edit time as well.
