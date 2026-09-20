using Xunit;

// La suite crea ViewModels que se suscriben al bus estático global (WeakReferenceMessenger.Default)
// y otros servicios con estado compartido: con clases en paralelo, mensajes difundidos por unas
// pruebas mutan el estado de otras a mitad de aserción (flakes intermitentes del área WPF/cliente,
// observados ~1 cada 15-30 corridas en tests distintos). Se serializa la ejecución para garantizar
// determinismo. Los aislamientos por prueba (WeakReferenceMessenger.Default.UnregisterAll) quedan
// como defensa en profundidad por si se re-habilita el paralelismo (candidato futuro: inyectar
// IMessenger en los ViewModels en lugar del estático).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
