using System.Reflection;
using System.Reflection.Emit;

namespace V.Script.Emit;

/// <summary>
/// Hands out types for generated code to live in, grouped into collectible assemblies.
/// </summary>
/// <remarks>
/// Creating a collectible assembly is what makes an asynchronous script two orders of magnitude
/// more expensive to compile than a synchronous one (§12), and it is a per-assembly cost, not a
/// per-type one. Putting several scripts in one assembly amortises it.
/// <para>
/// The trade is unloading granularity. One assembly per script means each script's code is
/// reclaimed the moment that script is disposed. A shared assembly can only unload once every
/// script in it is gone, so a single long-lived script pins its whole generation. That is why
/// the default is one script per assembly — the documented behaviour — and a host that compiles
/// scripts in batches opts in with <see cref="ScriptOptions.ScriptsPerGeneratedAssembly"/>.
/// </para>
/// </remarks>
internal sealed class GeneratedAssemblyPool(int scriptsPerAssembly)
{
    private readonly Lock _gate = new();
    private Generation? _current;
    private int _sequence;

    /// <summary>Reserves a type in the current generation, opening a new one when it is full.</summary>
    public TypeLease Define(string name)
    {
        lock (_gate)
        {
            _current ??= new Generation(_sequence++, scriptsPerAssembly);

            var lease = _current.Reserve(name);

            // Retire as soon as it is full rather than when the next one is asked for, so the
            // pool stops referencing it. Otherwise the most recently compiled script's assembly
            // would stay loaded until some later compile displaced it — and at the default of
            // one script per assembly, that would break "disposing a script unloads its code".
            if (_current.IsFull)
            {
                _current.Retire();
                _current = null;
            }

            return lease;
        }
    }

    /// <summary>One collectible assembly and the scripts sharing it.</summary>
    internal sealed class Generation
    {
        private readonly Lock _gate = new();
        private readonly int _capacity;

        private AssemblyBuilder? _assembly;
        private ModuleBuilder? _module;

        private int _reserved;
        private int _live;
        private bool _retired;

        public Generation(int index, int capacity)
        {
            _capacity = capacity;

            var name = new AssemblyName($"V.Script.Generated.{index}");
            _assembly = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.RunAndCollect);
            _module = _assembly.DefineDynamicModule("M");
        }

        public bool IsFull => Volatile.Read(ref _reserved) >= _capacity;

        public TypeLease Reserve(string name)
        {
            lock (_gate)
            {
                var type = _module!.DefineType(
                    $"Script{_reserved}",
                    TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

                _reserved++;
                _live++;

                return new TypeLease(this, type, name);
            }
        }

        public void Retire()
        {
            lock (_gate)
            {
                _retired = true;
                DropIfDone();
            }
        }

        public void Release()
        {
            lock (_gate)
            {
                _live--;
                DropIfDone();
            }
        }

        /// <summary>
        /// Lets go of the builders once the generation is closed and empty. Nothing else holds
        /// them, so this is what allows the runtime to unload the assembly.
        /// </summary>
        private void DropIfDone()
        {
            if (!_retired || _live > 0) return;

            _module = null;
            _assembly = null;
        }

        public string Describe() => _assembly?.FullName ?? "<unloaded>";
    }

    /// <summary>
    /// A script's claim on one generated type. Keeping it alive keeps the generated code loaded;
    /// disposing it is what lets the assembly go once every sibling has been disposed too.
    /// </summary>
    public sealed class TypeLease(GeneratedAssemblyPool.Generation generation, TypeBuilder builder, string name)
        : IDisposable
    {
        private Generation? _generation = generation;
        private Type? _created;

        public TypeBuilder Builder { get; } = builder;

        /// <summary>Pins the finished type so the script's delegate keeps working.</summary>
        public void Publish(Type created) => _created = created;

        public void Dispose()
        {
            var generation = Interlocked.Exchange(ref _generation, null);
            if (generation is null) return;

            _created = null;
            generation.Release();
        }

        public override string ToString() =>
            _generation is null ? "<released>" : $"{name} in {_generation.Describe()}";
    }
}
