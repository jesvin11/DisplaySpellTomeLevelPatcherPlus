using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.FormKeys.SkyrimSE;
using System.Threading.Tasks;
using System.Collections.Immutable;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Exceptions;
using Mutagen.Bethesda.Plugins.Cache;
using System.Globalization;
using Soltys.ChangeCase;

namespace DisplaySpellTomeLevelPatcher
{
    public static class Program
    {
        private static Lazy<Settings> _settings = null!;
        public static Task<int> Main(string[] args)
        {
            return SynthesisPipeline.Instance
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
                .SetAutogeneratedSettings(nickname: "Settings", path: "settings.json", out _settings)
                .SetTypicalOpen(GameRelease.SkyrimSE, "DisplaySpellTomeLevelPatcher.esp")
                .Run(args);
        }

        public readonly static ModKey BetterSpellLearning = ModKey.FromNameAndExtension("Better Spell Learning.esp");

        public const string LevelFormat = "<level>";
        public const string SpellFormat = "<spell>";
        public const string PluginFormat = "<plugin>";
        public const string ModFormat = "<mod>";
        public const string SchoolFormat = "<school>";

        public readonly static Dictionary<ActorValue, string> MagicSchools = new() {
            { ActorValue.Alteration, "Alteration" },
            { ActorValue.Conjuration, "Conjuration" },
            { ActorValue.Destruction, "Destruction" },
            { ActorValue.Illusion, "Illusion" },
            { ActorValue.Restoration, "Restoration" }
        };


        public static string GetSchoolName(ActorValue av)
        {
            if (MagicSchools.ContainsKey(av))
                return MagicSchools[av];

            return "None";
        }

        public static readonly HashSet<uint> AllowedMinimumSkillLevels = new() { 0, 25, 50, 75, 100 };
        public static Tuple<ActorValue, int>? GetSpellInfo(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, ISpellGetter spell)
        {
            double maxCost = -1.0;
            int maxLevel = -1;
            IMagicEffectGetter? maxBaseEffect = null;
            foreach (var effect in spell.Effects)
            {
                if (effect.Data == null)
                    continue;

                if (effect.BaseEffect.TryResolve(state.LinkCache, out var baseEffect))
                {
                    var durFactor = baseEffect.CastType == CastType.Concentration ? 1 : Math.Pow(effect.Data.Duration == 0 ? 10 : effect.Data.Duration, 1.1);
                    double cost = baseEffect.BaseCost * Math.Pow(effect.Data.Magnitude, 1.1) * durFactor;
                    if (cost > maxCost)
                    {
                        maxCost = cost;
                        maxBaseEffect = baseEffect;

                        if (!AllowedMinimumSkillLevels.Contains(baseEffect.MinimumSkillLevel))
                            Console.WriteLine("Unexpected minimum skill level for magic effect:" + baseEffect.FormKey);

                        maxLevel = (int)(baseEffect.MinimumSkillLevel / 25);
                        maxLevel = Math.Max(0, Math.Min(4, maxLevel));
                    }
                }
            }
            if (maxBaseEffect == null)
                return null;

            return new Tuple<ActorValue, int>(maxBaseEffect.MagicSkill, maxLevel);
        }

        // Gets the spell effect for Better Spell Learning affected tomes
        public static ISpellGetter? GetBSLSpell(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, IBookGetter book)
        {
            var spellTomeReadScript = book.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == "SpellTomeReadScript");
            IScriptObjectPropertyGetter? spellLearnedProperty = (IScriptObjectPropertyGetter?)(spellTomeReadScript?.Properties.FirstOrDefault(p => p is IScriptObjectPropertyGetter && p.Name == "SpellLearned"));

            if (spellLearnedProperty?.Object.TryResolve<ISpellGetter>(state.LinkCache, out var spell) ?? false)
                return spell;

            return null;
        }

        public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            bool bslActive = state.LoadOrder.PriorityOrder.ModExists(BetterSpellLearning);

            foreach (var bookContext in state.LoadOrder.PriorityOrder.Book().WinningContextOverrides())
            {
                var book = bookContext.Record;
                try
                {
                    if (book.Name?.String == null)
                        continue;

                    if (!book.Keywords?.Contains(Skyrim.Keyword.VendorItemSpellTome) ?? true)
                        continue;

                    ISpellGetter? spell = null;
                    if (book.Teaches is IBookSpellGetter teachedSpell && teachedSpell.Spell.TryResolve(state.LinkCache, out var resolvedSpell))
                        spell = resolvedSpell;
                    else if (spell == null && bslActive)
                        spell = GetBSLSpell(state, book);

                    if (spell == null || spell.Name == null)
                        continue;

                    var spellName = spell.Name.String;
                    var spellInfo = GetSpellInfo(state, spell);
                    var settings = _settings.Value;

                    var schoolName = "";
                    var levelName = "";
                    var modName = "";
                    if (settings.Format.Contains(ModFormat))
                    {
                        if (!settings.PluginModNamePairs.TryGetValue(bookContext.ModKey.FileName, out modName))
                        {
                            modName = SmartTryGenerateModName(bookContext.Record.FormKey.ModKey);
                        }
                    }

                    var newSpellName = modName + ": " + spellName;

                    var requiresSpellInfo = settings.Format.Contains(SchoolFormat) || settings.Format.Contains(LevelFormat);
                    if (requiresSpellInfo)
                    {
                        if (spellInfo == null)
                        {
                            Console.WriteLine("Cannot determine school and level for book: " + book.Name.String);
                            continue;
                        }
                        var school = spellInfo.Item1;
                        schoolName = MagicSchools[school];


                        var level = spellInfo.Item2;
                        levelName = settings.LevelNames[level];
                    }
                    var pluginName = book.FormKey.ModKey.Name;


                    var newName = settings.Format.Replace(LevelFormat, levelName).Replace(PluginFormat, pluginName).Replace(SchoolFormat, schoolName).Replace(SpellFormat, spellName).Replace(ModFormat, modName);

                    Console.WriteLine(book.Name.String + "->" + newName);

                    state.PatchMod.Spells.GetOrAddAsOverride(spell).Name = newSpellName

                    state.PatchMod.Books.GetOrAddAsOverride(book).Name = newName;
                }
                catch (Exception e)
                {
                    Console.WriteLine(RecordException.Enrich(e, bookContext.ModKey, book).ToString());
                }
            }
        }

        private static string SmartTryGenerateModName(ModKey modKey) => modKey.Name.TitleCase();

    }
}
