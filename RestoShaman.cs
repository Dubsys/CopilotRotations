using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using Styx;
using Styx.Combat.CombatRoutine; 
using Styx.Helpers;
using Styx.Logic.Combat;
using Styx.Logic.Pathing;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using TreeSharp;
using Action = TreeSharp.Action;

namespace SmartRestoShaman
{
    public enum PauseKeyMode { None, LeftControl, LeftShift, LeftAlt }

    public class RestoSettings : Styx.Helpers.Settings
    {
        private static RestoSettings _instance;
        public static RestoSettings Instance { get { return _instance ?? (_instance = new RestoSettings()); } }

        public RestoSettings() : base(Styx.Helpers.Logging.ApplicationPath + "\\Settings\\RestoShamanSettings_" + StyxWoW.Me.Name + ".xml") { Load(); }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("1. Spell Queueing (Latency Optimizer)")]
        [DisplayName("Enable Cast Queueing")]
        public bool EnableSpellQueueing { get; set; }

        [Setting, Styx.Helpers.DefaultValue(300)]
        [Category("1. Spell Queueing (Latency Optimizer)")]
        [DisplayName("Queue Window (ms)")]
        public int SpellQueueWindow { get; set; }

        [Setting, Styx.Helpers.DefaultValue(600)]
        [Category("1. Spell Queueing (Latency Optimizer)")]
        [DisplayName("Target Lockout (ms)")]
        [Description("Prevents double-casting on the same target until the server updates their HP.")]
        public int TargetLockoutMs { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("2. Utility")]
        [DisplayName("Cure Toxins / Cleanse Spirit")]
        public bool CureToxins { get; set; }

        [Setting, Styx.Helpers.DefaultValue(3000)]
        [Category("2. Utility")]
        [DisplayName("Dispel Throttle (ms)")]
        [Description("Minimum delay between dispel casts to prevent endless dispel spam.")]
        public int DispelThrottleMs { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("3. Combat Options")]
        [DisplayName("Auto Interrupt (Wind Shear)")]
        public bool AutoInterrupt { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("3. Combat Options")]
        [DisplayName("Auto Assist / DPS")]
        public bool AutoAssist { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("3. Combat Options")]
        [DisplayName("Disable DPS On Low Mana")]
        public bool DisableDPSLowMana { get; set; }

        [Setting, Styx.Helpers.DefaultValue(60)]
        [Category("3. Combat Options")]
        [DisplayName("DPS Mana Threshold (%)")]
        public int DPSManaThreshold { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("4. Totems")]
        [DisplayName("Auto Totems")]
        public bool AutoTotems { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("4. Totems")]
        [DisplayName("Use Mana Tide Totem")]
        public bool UseManaTide { get; set; }

        [Setting, Styx.Helpers.DefaultValue(35)]
        [Category("4. Totems")]
        [DisplayName("Mana Tide Threshold (%)")]
        public int ManaTideThreshold { get; set; }

        [Setting, Styx.Helpers.DefaultValue(PauseKeyMode.LeftControl)]
        [Category("5. Hotkeys & Overrides")]
        [DisplayName("Pause Modifier Key")]
        public PauseKeyMode PauseModifierKey { get; set; }
    }

    public class RestoShamanCR : CombatRoutine
    {
        public override string Name { get { return "[RESTO] Resto Shaman (Dynamic Scaling)"; } }
        public override WoWClass Class { get { return WoWClass.Shaman; } }
        public override bool NeedRest { get { return StyxWoW.Me.ManaPercent < 20 && !StyxWoW.Me.Combat; } }
        public override double? PullDistance { get { return 28.0; } }

        public override bool WantButton { get { return true; } }
        public override void OnButtonPress()
        {
            Form configForm = new Form { Text = "Restoration Shaman Settings", Width = 420, Height = 640, StartPosition = FormStartPosition.CenterScreen };
            PropertyGrid grid = new PropertyGrid { SelectedObject = RestoSettings.Instance, Dock = DockStyle.Fill };
            configForm.Controls.Add(grid);
            configForm.FormClosing += (s, e) => RestoSettings.Instance.Save();
            configForm.ShowDialog();
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        private const int VK_LCONTROL = 0xA2;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_LMENU = 0xA4;

        private bool IsPauseModifierPressed()
        {
            switch (RestoSettings.Instance.PauseModifierKey)
            {
                case PauseKeyMode.LeftControl: return (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0;
                case PauseKeyMode.LeftShift: return (GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0;
                case PauseKeyMode.LeftAlt: return (GetAsyncKeyState(VK_LMENU) & 0x8000) != 0;
                default: return false;
            }
        }

        private DateTime _lastEnvironmentUpdate = DateTime.MinValue;
        private DateTime _lastDispelTime = DateTime.MinValue;
        private ulong _lastHealedTargetGuid = 0;
        private DateTime _lastHealTime = DateTime.MinValue;

        private List<WoWPlayer> _cachedGroup = new List<WoWPlayer>();
        private List<WoWPlayer> _cachedTanks = new List<WoWPlayer>();
        private WoWPlayer _cachedTank = null;
        private WoWPlayer _cachedHealTarget = null;
        private WoWUnit _cachedAssistTarget = null;
        private bool _needsTremor = false;
        private bool _needsCleansing = false;
        private WoWPlayer _cureTarget = null;
        private bool _isInParty = false;
        private bool _isPatchwerkFight = false;
        
        // Group Size States
        private bool _is25Man = false;
        private bool _is10Man = false;
        private bool _is5Man = true;

        private Dictionary<ulong, int> _lastHealth = new Dictionary<ulong, int>();
        private Dictionary<ulong, DateTime> _lastTime = new Dictionary<ulong, DateTime>();
        private Dictionary<ulong, double> _damagePerSecond = new Dictionary<ulong, double>();

        private Composite _combatBehavior;
        public override Composite CombatBehavior { get { return _combatBehavior ?? (_combatBehavior = CreateCombatBehavior()); } }

        private Composite _pullBehavior;
        public override Composite PullBehavior { get { return _pullBehavior ?? (_pullBehavior = CombatBehavior); } }

        private Composite _restBehavior;
        public override Composite RestBehavior { get { return _restBehavior ?? (_restBehavior = CreateRestBehavior()); } }

        private Composite CreateRestBehavior()
        {
            return new PrioritySelector(
                new Decorator(ret => IsPauseModifierPressed(), new Action(ret => RunStatus.Success)),
                new Action(ret => { UpdateEnvironment(); return RunStatus.Failure; }),
                new Decorator(ret => StyxWoW.Me.HasAura("Food") || StyxWoW.Me.HasAura("Drink"), new Action(ret => RunStatus.Success)),
                CreateEarthShieldBlock(),
                CreateHealBehavior(),
                CreateBuffBehavior()
            );
        }

        private Composite CreateCombatBehavior()
        {
            return new PrioritySelector(
                new Decorator(ret => IsPauseModifierPressed(), new Action(ret => RunStatus.Success)),
                new Action(ret => { UpdateEnvironment(); return RunStatus.Failure; }),

                // Off-GCD Interrupts
                new Decorator(ret => RestoSettings.Instance.AutoInterrupt && StyxWoW.Me.CurrentTarget != null && (StyxWoW.Me.CurrentTarget.IsCasting || StyxWoW.Me.CurrentTarget.IsChanneling) && StyxWoW.Me.CurrentTarget.CanInterruptCurrentSpellCast && SpellManager.CanCast("Wind Shear"),
                    new Action(ret => {
                        Logging.Write("Interrupting with Wind Shear (Off-GCD)");
                        SpellManager.Cast("Wind Shear");
                        return RunStatus.Failure; 
                    })),

                CreateBossPurgeBehavior(),
                CreateEarthShieldBlock(),
                CreateHealBehavior(),
                CreateBuffBehavior(),
                CreateTotemBehavior(),
                CreateDPSBehavior()
            );
        }

        private Composite CreateBossPurgeBehavior()
        {
            return new Decorator(ret => StyxWoW.Me.CurrentTarget != null 
                                     && StyxWoW.Me.CurrentTarget.IsAlive 
                                     && StyxWoW.Me.CurrentTarget.IsHostile 
                                     && IsBossOrElite(StyxWoW.Me.CurrentTarget) 
                                     && TargetHasPurgeableAura(StyxWoW.Me.CurrentTarget) 
                                     && SpellManager.CanCast("Purge"),
                new PrioritySelector(
                    new Decorator(ret => !StyxWoW.Me.IsFacing(StyxWoW.Me.CurrentTarget), new Action(ret => { StyxWoW.Me.CurrentTarget.Face(); return RunStatus.Failure; })),
                    CastSpell("Purge")
                ));
        }

        private Composite CreateEarthShieldBlock()
        {
            return new Decorator(ret => _isInParty && _cachedTank != null && _cachedTank.Guid != StyxWoW.Me.Guid && _cachedTank.DistanceSqr < 1600 && !_cachedTank.HasAura("Earth Shield") && SpellManager.CanCast("Earth Shield") && IsSafeToHeal(false),
                new Action(ret => {
                    Logging.Write("Maintaining Earth Shield on Tank (Targetless): " + _cachedTank.Name);
                    SpellManager.Cast("Earth Shield", _cachedTank);
                    return RunStatus.Success;
                }));
        }

        private Composite CreateBuffBehavior()
        {
            return new PrioritySelector(
                new Decorator(ret => !HasWeaponImbue() && SpellManager.CanCast("Earthliving Weapon"), CastSpell("Earthliving Weapon")),
                new Decorator(ret => !StyxWoW.Me.HasAura("Water Shield") && SpellManager.CanCast("Water Shield"), CastSpell("Water Shield"))
            );
        }

        private Composite CreateTotemBehavior()
        {
            return new PrioritySelector(
                new Decorator(ret => RestoSettings.Instance.UseManaTide && StyxWoW.Me.ManaPercent < RestoSettings.Instance.ManaTideThreshold && SpellManager.CanCast("Mana Tide Totem"), CastSpell("Mana Tide Totem")),
                new Decorator(ret => RestoSettings.Instance.AutoTotems && _needsTremor && !HasTotem("Tremor Totem") && SpellManager.CanCast("Tremor Totem"), CastSpell("Tremor Totem")),
                new Decorator(ret => RestoSettings.Instance.AutoTotems && _needsCleansing && !HasTotem("Cleansing Totem") && SpellManager.CanCast("Cleansing Totem"), CastSpell("Cleansing Totem")),
                new Decorator(ret => RestoSettings.Instance.AutoTotems && StyxWoW.Me.Combat && GetActiveTotemCount() < 3 && !HasAnyTempTotem() && SpellManager.CanCast("Call of the Spirits"),
                    new Action(ret => {
                        Logging.Write("Fewer than 3 totems active in combat. Dropping Call of the Spirits.");
                        SpellManager.Cast("Call of the Spirits");
                        return RunStatus.Success;
                    }))
            );
        }

        private Composite CreateHealBehavior()
        {
            return new Decorator(ret => _cachedHealTarget != null && IsSafeToHeal(_cachedHealTarget.HealthPercent < 30),
                new PrioritySelector(
                    new Decorator(ret => StyxWoW.Me.IsMoving && _cachedHealTarget.HealthPercent < 80, new Action(ret => { WoWMovement.MoveStop(); return RunStatus.Failure; })),

                    new Decorator(ret => _cachedHealTarget.HealthPercent < 30 && SpellManager.CanCast("Nature's Swiftness") && SpellManager.CanCast("Healing Wave"),
                        new Action(ret => {
                            Logging.Write("EMERGENCY (Targetless): Nature's Swiftness + Healing Wave on " + _cachedHealTarget.Name);
                            _lastHealedTargetGuid = _cachedHealTarget.Guid;
                            _lastHealTime = DateTime.Now;
                            SpellManager.Cast("Nature's Swiftness");
                            SpellManager.Cast("Healing Wave", _cachedHealTarget);
                            return RunStatus.Success;
                        })),

                    new Decorator(ret => _cachedHealTarget.HealthPercent < 40 && !StyxWoW.Me.HasAura("Tidal Force") && SpellManager.CanCast("Tidal Force"),
                        new Action(ret => {
                            Logging.Write("Using Tidal Force for guaranteed critical heals on " + _cachedHealTarget.Name);
                            SpellManager.Cast("Tidal Force");
                            return RunStatus.Success;
                        })),
                    
                    // Dynamic Riptide: Cast aggressively in 25-man to fish for Tidal Waves; hold for spot damage in 5/10-man.
                    new Decorator(ret => _cachedHealTarget.HealthPercent < (_is25Man ? 95 : 85) && !_cachedHealTarget.HasAura("Riptide") && SpellManager.CanCast("Riptide"), CastHeal("Riptide")),
                    
                    // Dynamic Chain Heal Scaling (Disabled on Patchwerk to prevent bounces from topping off melee DPS)
                    new Decorator(ret => !_isPatchwerkFight && SpellManager.CanCast("Chain Heal") && (
                        (_is25Man && _cachedHealTarget.HealthPercent <= 95 && GetGroupMembersBelowPercent(90, _cachedHealTarget.Location, 12.0) >= 3) ||
                        (_is10Man && _cachedHealTarget.HealthPercent <= 85 && GetGroupMembersBelowPercent(85, _cachedHealTarget.Location, 12.0) >= 3) ||
                        (_is5Man && _cachedHealTarget.HealthPercent <= 80 && GetGroupMembersBelowPercent(80, _cachedHealTarget.Location, 12.0) >= 3)),
                        CastHeal("Chain Heal")),

                    // Dispel Logic (Throttled & placed below critical < 60% healing)
                    new Decorator(ret => RestoSettings.Instance.CureToxins && _cureTarget != null && _cachedHealTarget.HealthPercent > 60 && (DateTime.Now - _lastDispelTime).TotalMilliseconds >= RestoSettings.Instance.DispelThrottleMs,
                        new Action(ret => {
                            string dispelSpell = SpellManager.HasSpell("Cleanse Spirit") ? "Cleanse Spirit" : "Cure Toxins";
                            if (SpellManager.CanCast(dispelSpell))
                            {
                                Logging.Write($"Dispelling (Targetless) {_cureTarget.Name} with {dispelSpell} (Throttled)");
                                SpellManager.Cast(dispelSpell, _cureTarget);
                                _lastDispelTime = DateTime.Now;
                                return RunStatus.Success;
                            }
                            return RunStatus.Failure;
                        })),

                    // Tidal Waves Synergy
                    new Decorator(ret => _cachedHealTarget.HealthPercent < 70 && StyxWoW.Me.HasAura("Tidal Waves") && SpellManager.CanCast("Healing Wave"), CastHeal("Healing Wave")),
                    new Decorator(ret => _cachedHealTarget.HealthPercent < 85 && StyxWoW.Me.HasAura("Tidal Waves") && SpellManager.CanCast("Lesser Healing Wave"), CastHeal("Lesser Healing Wave")),

                    // Standard Fallbacks
                    new Decorator(ret => _cachedHealTarget.HealthPercent < 70 && SpellManager.CanCast("Lesser Healing Wave"), CastHeal("Lesser Healing Wave")),
                    new Decorator(ret => _cachedHealTarget.HealthPercent < 85 && SpellManager.CanCast("Healing Wave"), CastHeal("Healing Wave"))
                ));
        }

        private Composite CreateDPSBehavior()
        {
            return new Decorator(ret => (!RestoSettings.Instance.DisableDPSLowMana || StyxWoW.Me.ManaPercent >= RestoSettings.Instance.DPSManaThreshold) && IsSafeToDPS(),
                new PrioritySelector(
                    new Decorator(ret => RestoSettings.Instance.AutoAssist && _cachedHealTarget == null && (StyxWoW.Me.CurrentTarget == null || !StyxWoW.Me.CurrentTarget.IsAlive || !StyxWoW.Me.CurrentTarget.IsHostile),
                        new Action(ret => {
                            if (_cachedAssistTarget != null && StyxWoW.Me.CurrentTargetGuid != _cachedAssistTarget.Guid)
                            {
                                Logging.Write("Assisting party against: " + _cachedAssistTarget.Name);
                                _cachedAssistTarget.Target();
                                return RunStatus.Success; 
                            }
                            return RunStatus.Failure;
                        })),
                    
                    new Decorator(ret => _cachedHealTarget == null && StyxWoW.Me.CurrentTarget != null && StyxWoW.Me.CurrentTarget.IsAlive && StyxWoW.Me.CurrentTarget.IsHostile,
                        new PrioritySelector(
                            new Decorator(ret => !StyxWoW.Me.IsFacing(StyxWoW.Me.CurrentTarget), new Action(ret => { StyxWoW.Me.CurrentTarget.Face(); return RunStatus.Failure; })),
                            new Decorator(ret => !StyxWoW.Me.CurrentTarget.HasAura("Flame Shock"), CastSpell("Flame Shock")),
                            CastSpell("Lava Burst"),
                            CastSpell("Lightning Bolt")
                        ))
                ));
        }

        // ------------------------------------------------------
        // RAID SAFETY & DEBUFF TRACKERS
        // ------------------------------------------------------
        private int GetAuraStacks(string auraName)
        {
            if (StyxWoW.Me.ActiveAuras.ContainsKey(auraName)) return (int)StyxWoW.Me.ActiveAuras[auraName].StackCount;
            return 0;
        }

        private bool IsSafeToHeal(bool isEmergency = false)
        {
            if (StyxWoW.Me.HasAura("Necrotic Aura")) return false; // Loatheb
            if (GetAuraStacks("Instability") >= 4 && !isEmergency) return false; // Sindragosa
            return true;
        }

        private bool IsSafeToDPS()
        {
            if (GetAuraStacks("Instability") >= 4) return false; // Sindragosa
            return true;
        }

        // ------------------------------------------------------
        // CORE SCANNER
        // ------------------------------------------------------
        private void UpdateEnvironment()
        {
            if ((DateTime.Now - _lastEnvironmentUpdate).TotalMilliseconds < 200) return;

            _cachedGroup.Clear();
            _cachedGroup.Add(StyxWoW.Me);
            _cachedTank = null;
            _needsTremor = false;
            _needsCleansing = false;
            _cureTarget = null;
            
            _isInParty = StyxWoW.Me.GroupInfo.IsInParty || StyxWoW.Me.GroupInfo.IsInRaid;
            bool hasCleanseSpirit = SpellManager.HasSpell("Cleanse Spirit");
            double closestAssistDistSqr = 999999;
            _cachedAssistTarget = null;

            foreach (WoWPlayer p in ObjectManager.GetObjectsOfType<WoWPlayer>())
            {
                if (p == null || !p.IsValid || !p.IsAlive || p.IsMe || !p.IsInMyPartyOrRaid) continue;

                if (p.DistanceSqr < 1600)
                {
                    _cachedGroup.Add(p);

                    // Maintain the primary MT for Earth Shield
                    if (_cachedTank == null || p.MaxHealth > _cachedTank.MaxHealth) _cachedTank = p;
                    if (p.Fleeing) _needsTremor = true;
                        
                    foreach (WoWAura aura in p.ActiveAuras.Values)
                    {
                        if (aura.Spell.DispelType == WoWDispelType.Poison || 
                            aura.Spell.DispelType == WoWDispelType.Disease || 
                            (hasCleanseSpirit && aura.Spell.DispelType == WoWDispelType.Curse))
                        {
                            _needsCleansing = true;
                            if (_cureTarget == null) _cureTarget = p;
                        }
                    }
                }
            }
            
            // Dynamic Group Size Detection
            _is25Man = _cachedGroup.Count > 10;
            _is10Man = _cachedGroup.Count > 5 && _cachedGroup.Count <= 10;
            _is5Man = _cachedGroup.Count <= 5;

            // Dynamic Multi-Tank Detection (Grabs the players with the highest Max HP pools)
            int tankCount = _is25Man ? 3 : (_is10Man ? 2 : 1);
            _cachedTanks = _cachedGroup.OrderByDescending(p => p.MaxHealth).Take(tankCount).ToList();

            _isPatchwerkFight = false;

            foreach (WoWUnit u in ObjectManager.GetObjectsOfType<WoWUnit>())
            {
                if (u == null || !u.IsValid || !u.IsHostile || !u.IsAlive) continue;
                double distSqr = u.DistanceSqr; 
                if (distSqr >= 1600) continue;

                // Patchwerk Detection
                if (u.Entry == 16028 && u.Combat) _isPatchwerkFight = true;

                if (u.CanSelect && u.InLineOfSight)
                {
                    bool attackingGroup = false;
                    if (u.CurrentTargetGuid != 0 && (u.CurrentTargetGuid == StyxWoW.Me.Guid || _cachedGroup.Any(g => g.Guid == u.CurrentTargetGuid)))
                    {
                        attackingGroup = true;
                    }

                    if (attackingGroup && distSqr < closestAssistDistSqr)
                    {
                        closestAssistDistSqr = distSqr;
                        _cachedAssistTarget = u;
                    }
                }
            }

            UpdateMemoryMatrices();
            _cachedHealTarget = DetermineBestHealTarget();
            _lastEnvironmentUpdate = DateTime.Now;
        }

        private void UpdateMemoryMatrices()
        {
            DateTime now = DateTime.Now;
            for (int i = 0; i < _cachedGroup.Count; i++)
            {
                WoWPlayer p = _cachedGroup[i];
                ulong guid = p.Guid;

                if (_lastHealth.ContainsKey(guid) && _lastTime.ContainsKey(guid))
                {
                    double timeDiff = (now - _lastTime[guid]).TotalSeconds;
                    if (timeDiff >= 0.5)
                    {
                        int hpDiff = _lastHealth[guid] - (int)p.CurrentHealth;
                        if (hpDiff > 0)
                        {
                            double dps = hpDiff / timeDiff;
                            _damagePerSecond[guid] = _damagePerSecond.ContainsKey(guid) ? (_damagePerSecond[guid] * 0.5) + (dps * 0.5) : dps;
                        }
                        else if (hpDiff < 0 && _damagePerSecond.ContainsKey(guid))
                        {
                            _damagePerSecond[guid] = _damagePerSecond[guid] * 0.5;
                        }

                        _lastHealth[guid] = (int)p.CurrentHealth;
                        _lastTime[guid] = now;
                    }
                }
                else
                {
                    _lastHealth[guid] = (int)p.CurrentHealth;
                    _lastTime[guid] = now;
                    _damagePerSecond[guid] = 0;
                }
            }
        }

        private bool NeedsHealing(WoWPlayer p)
        {
            bool isTank = _cachedTanks.Any(t => t.Guid == p.Guid) || p.IsMe;

            if (_isPatchwerkFight)
            {
                // Patchwerk Override: Keep DPS heavily injured so they do not soak Hateful Strikes
                return isTank ? p.HealthPercent < 95 : p.HealthPercent < 20;
            }

            return isTank ? p.HealthPercent < 85 : p.HealthPercent < 60;
        }

        private WoWPlayer DetermineBestHealTarget()
        {
            WoWPlayer bestTarget = null;
            double highestPriority = -10000;
            
            // Bypass lockout if target is in extreme danger (< 20% HP)
            for (int i = 0; i < _cachedGroup.Count; i++)
            {
                if (_cachedGroup[i].HealthPercent < 20) return _cachedGroup[i];
            }

            for (int i = 0; i < _cachedGroup.Count; i++)
            {
                WoWPlayer p = _cachedGroup[i];

                // Target Lockout Check
                if (p.Guid == _lastHealedTargetGuid && (DateTime.Now - _lastHealTime).TotalMilliseconds < RestoSettings.Instance.TargetLockoutMs)
                {
                    continue;
                }

                if (NeedsHealing(p)) 
                {
                    double missingHp = p.MaxHealth - p.CurrentHealth;
                    double dps = _damagePerSecond.ContainsKey(p.Guid) ? _damagePerSecond[p.Guid] : 0;
                    double predictedMissingHp = missingHp + (dps * 2.0);
                    double score = (predictedMissingHp / p.MaxHealth) * 100.0;
                    
                    if (p.HasAura("Earth Shield")) score += 25; 

                    if (score > highestPriority)
                    {
                        highestPriority = score;
                        bestTarget = p;
                    }
                }
            }
            return bestTarget;
        }

        private Composite CastHeal(string spellName)
        {
            return new Decorator(ret => CustomCanCast(spellName),
                new Action(ret =>
                {
                    if (_cachedHealTarget == null) return RunStatus.Failure;
                    Logging.Write(string.Format("Casting (Targetless): {0} on {1}", spellName, _cachedHealTarget.Name));
                    _lastHealedTargetGuid = _cachedHealTarget.Guid;
                    _lastHealTime = DateTime.Now;
                    SpellManager.Cast(spellName, _cachedHealTarget);
                    return RunStatus.Success;
                }));
        }

        private Composite CastSpell(string spellName)
        {
            return new Decorator(ret => CustomCanCast(spellName),
                new Action(ret =>
                {
                    Logging.Write(string.Format("Casting: {0}", spellName));
                    SpellManager.Cast(spellName);
                    return RunStatus.Success;
                }));
        }

        private bool CustomCanCast(string spellName)
        {
            if (!SpellManager.HasSpell(spellName)) return false;

            if (!StyxWoW.Me.IsCasting && !StyxWoW.Me.IsChanneling) return SpellManager.CanCast(spellName);

            if (RestoSettings.Instance.EnableSpellQueueing && StyxWoW.Me.IsCasting)
            {
                if (StyxWoW.Me.CurrentCastTimeLeft.TotalMilliseconds > 0 && StyxWoW.Me.CurrentCastTimeLeft.TotalMilliseconds <= RestoSettings.Instance.SpellQueueWindow)
                {
                    if (SpellManager.Spells.ContainsKey(spellName) && SpellManager.Spells[spellName].CooldownTimeLeft.TotalMilliseconds == 0) return true; 
                }
            }
            return false;
        }

        private double Distance(WoWPoint a, WoWPoint b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            double dz = a.Z - b.Z;
            return Math.Sqrt(dx*dx + dy*dy + dz*dz);
        }

        private int GetGroupMembersBelowPercent(double percent, WoWPoint center, double radius)
        {
            int count = 0;
            for (int i = 0; i < _cachedGroup.Count; i++)
            {
                if (_cachedGroup[i].HealthPercent <= percent && Distance(_cachedGroup[i].Location, center) <= radius) count++;
            }
            return count;
        }

        private bool HasWeaponImbue()
        {
            var mainHand = StyxWoW.Me.Inventory.Equipped.MainHand;
            return mainHand != null && mainHand.TemporaryEnchantment != null && mainHand.TemporaryEnchantment.Id > 0;
        }
        
        private int GetActiveTotemCount() { return StyxWoW.Me.Totems.Count(t => t != null && t.Unit != null); }
        
        private bool HasTotem(string totemName) { return StyxWoW.Me.Totems.Any(t => t != null && t.Unit != null && t.Name == totemName); }

        private bool HasAnyTempTotem()
        {
            string[] tempTotems = { "Fire Elemental Totem", "Earth Elemental Totem", "Mana Tide Totem", "Tremor Totem", "Cleansing Totem" };
            for (int i = 0; i < tempTotems.Length; i++)
            {
                if (HasTotem(tempTotems[i])) return true;
            }
            return false;
        }

        private bool IsBossOrElite(WoWUnit u)
        {
            return u.IsBoss || u.Elite || u.MaxHealth > (StyxWoW.Me.MaxHealth * 3);
        }

        private bool TargetHasPurgeableAura(WoWUnit u)
        {
            foreach (WoWAura aura in u.ActiveAuras.Values)
            {
                if (aura.Spell.DispelType == WoWDispelType.Magic && aura.IsHarmful == false) return true;
            }
            return false;
        }
    }
}