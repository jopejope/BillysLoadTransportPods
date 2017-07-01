using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using UnityEngine;
using System.Reflection;
using System.Diagnostics;

namespace BillysLoadTransport
{
    [DefOf]
    public class BillyJobDefOf
    {
        public static JobDef BillyTransportHaulToTransporter;
    }

    public static class BillyLoadTransporterUtility
    {
        // this is based directly on GatherItemsForCaravanUtility, since that class also deals with the issue of 
        // coordinating multiple colonists loading the same set of things at the same time.
        // Many functions also brought in from LoadTransportersJobUtility, except modified to not have each
        // colonist reserve the transport pod itself.

        private static HashSet<Thing> neededThings = new HashSet<Thing>();
        private static HashSet<ThingDef> neededThingDefs = new HashSet<ThingDef>();

        public static bool HasJobOnTransporter(Pawn pawn, CompTransporter transporter)
        {
            return transporter != null &&
                !transporter.parent.IsForbidden(pawn) && 
                transporter.AnythingLeftToLoad && 
                pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) && 
                pawn.CanReach(transporter.parent, PathEndMode.Touch, pawn.NormalMaxDanger(), false, TraverseMode.ByPawn) && 
                FindThingToLoad(pawn, transporter) != null;
        }

        public static Job JobOnTransporter(Pawn p, CompTransporter transporter)
        {
            Thing thing = FindThingToLoad(p, transporter);
            Job job = new Job(BillyJobDefOf.BillyTransportHaulToTransporter, thing, transporter.parent);
            int countToTransfer = TransferableUtility.TransferableMatchingDesperate(thing, transporter.leftToLoad).CountToTransfer;
            job.count = Mathf.Min(countToTransfer, thing.stackCount);
            job.ignoreForbidden = true;
            return job;
        }

        public static Thing FindThingToLoad(Pawn p, CompTransporter transporter)
        {
            neededThings.Clear();
            neededThingDefs.Clear();
            List<TransferableOneWay> transferables = transporter.leftToLoad;
            if (transferables != null)
            {
                for (int i = 0; i < transferables.Count; i++)
                {
                    TransferableOneWay transferableOneWay = transferables[i];
                    if (CountLeftToTransfer(p, transferableOneWay, transporter) > 0)
                    {
                        for (int j = 0; j < transferableOneWay.things.Count; j++)
                        {
                            Thing t = transferableOneWay.things[j];
                            neededThings.Add(t);
                            if(t.def.category == ThingCategory.Item) neededThingDefs.Add(t.def);
                        }
                    }
                }
            }
            if (!neededThings.Any<Thing>())
            {
                return null;
            }
            Predicate<Thing> validator = (Thing x) => neededThings.Contains(x) && p.CanReserve(x, 1, -1, null, false);
            Thing thing = FindClosestReachable(p, validator);
            if (thing == null)
            {
                foreach (Thing current in neededThings)
                {
                    Pawn pawn = current as Pawn;
                    if (pawn != null && (!pawn.IsColonist || pawn.Downed) && p.CanReserveAndReach(pawn, PathEndMode.Touch, Danger.Deadly, 1, -1, null, false))
                    {
                        return pawn;
                    }
                }
            }
            if (neededThingDefs.Any<ThingDef>())
            {
                if (thing == null)
                {
                    Predicate<Thing> validator2 = (Thing x) => neededThings.Contains(x);
                    if (FindClosestReachable(p, validator2) != null) return null; // there is something left to haul, but someone else has reserved it
                }
                if (thing == null)
                {
                    // some things are missing or unreachable. We'll see if we can find some suitable substitute
                    Predicate<Thing> validator3 = (Thing x) =>
                        {
                            if (x is Pawn || x.def.category != ThingCategory.Item || !neededThingDefs.Contains(x.def) || !p.CanReserve(x, 1, -1, null, false)) return false;
                            foreach (Thing t in neededThings)
                            {
                                if (TransferableUtility.TransferAsOne(t, x)) return true;
                            }
                            return false;
                        };
                    thing = FindClosestReachable(p, validator3);
                }
            }
            neededThings.Clear();
            neededThingDefs.Clear();
            return thing;
        }

        private static Thing FindClosestReachable(Pawn p, Predicate<Thing> validator)
        {
            return GenClosest.ClosestThingReachable(p.Position, p.Map, ThingRequest.ForGroup(ThingRequestGroup.HaulableEver), PathEndMode.Touch, TraverseParms.For(p, Danger.Deadly, TraverseMode.ByPawn, false), 9999f, validator, null, 0, -1, false, RegionType.Set_Passable, false);
        }

        public static int CountLeftToTransfer(Pawn pawn, TransferableOneWay transferable, CompTransporter transporter)
        {
            if (transferable.CountToTransfer <= 0 || !transferable.HasAnyThing)
            {
                return 0;
            }
            return Mathf.Max(transferable.CountToTransfer - TransferableCountHauledByOthers(pawn, transferable, transporter), 0);
        }

        private static int TransferableCountHauledByOthers(Pawn pawn, TransferableOneWay transferable, CompTransporter transporter)
        {
            if (!transferable.HasAnyThing)
            {
                Log.Warning("BillyCaravan: Can't determine transferable count hauled by others because transferable has 0 things.");
                return 0;
            }
            if (transporter == null)
            {
                Log.Warning("BillyCaravan: transporter is null in count hauled by others.");
                return 0;
            }
            List<Pawn> allPawnsSpawned = transporter.Map.mapPawns.AllPawnsSpawned;
            int num = 0;
            for (int i = 0; i < allPawnsSpawned.Count; i++)
            {
                Pawn pawn2 = allPawnsSpawned[i];
                if (pawn2 != pawn)
                {
                    if (pawn2.CurJob != null && pawn2.CurJob.def == BillyJobDefOf.BillyTransportHaulToTransporter && pawn2.CurJob.targetB.Thing == transporter.parent)
                    {
                        Thing toHaul = pawn2.CurJob.targetA.Thing;
                        if (transferable.things.Contains(toHaul) || TransferableUtility.TransferAsOne(transferable.AnyThing, toHaul))
                        {
                            num += toHaul.stackCount;
                        }
                    }
                }
            }
            return num;
        }
    }

    public class JobDriver_HaulToTransporter : JobDriver_HaulToContainer
    {
        public Thing ToHaul
        {
            get
            {
                return base.CurJob.GetTarget(TargetIndex.A).Thing;
            }
        }

        public Thing Transporter
        {
            get
            {
                return base.CurJob.GetTarget(TargetIndex.B).Thing;
            }
        }

        public List<TransferableOneWay> Transferables
        {
            get
            {
                return Transporter.TryGetComp<CompTransporter>().leftToLoad;
            }
        }

        public TransferableOneWay Transferable
        {
            get
            {
                TransferableOneWay transferableOneWay = TransferableUtility.TransferableMatchingDesperate(this.ToHaul, this.Transferables);
                if (transferableOneWay != null)
                {
                    return transferableOneWay;
                }
                throw new InvalidOperationException("Could not find any matching transferable.");
            }
        }

        [DebuggerHidden]
		protected override IEnumerable<Toil> MakeNewToils()
		{
            // This is mostly the same list of toils as HaulToContainer, except without reserving the container,
            // and also added some things from JobDriver_PrepareCaravan_GatherItems that handle coordination between
            // colonists. Many of the toils here seem to be intended for construction jobs and are thus probably not
            // necessary, but I'm leaving them in just in case. Possibly another mod will assume they're there, who knows.
			this.FailOnDestroyedOrNull(TargetIndex.A);
			this.FailOnDestroyedNullOrForbidden(TargetIndex.B);
			this.FailOn(() => TransporterUtility.WasLoadingCanceled(this.Transporter));
            Toil reserve = Toils_Reserve.Reserve(TargetIndex.A, 1, -1, null);
            yield return reserve;
			yield return Toils_Reserve.ReserveQueue(TargetIndex.A, 1, -1, null);
			Toil getToHaulTarget = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch).FailOnSomeonePhysicallyInteracting(TargetIndex.A);
			yield return getToHaulTarget;
            yield return DetermineNumToHaul();
			yield return Toils_Construct.UninstallIfMinifiable(TargetIndex.A).FailOnSomeonePhysicallyInteracting(TargetIndex.A);
			yield return Toils_Haul.StartCarryThing(TargetIndex.A, false, true);
            yield return AddCarriedThingToTransferables();
            yield return Toils_Haul.CheckForGetOpportunityDuplicate(reserve, TargetIndex.A, TargetIndex.None, true, (Thing x) => Transferable.things.Contains(x));
			yield return Toils_Haul.JumpIfAlsoCollectingNextTargetInQueue(getToHaulTarget, TargetIndex.A);
			Toil carryToContainer = Toils_Haul.CarryHauledThingToContainer();
			yield return carryToContainer;
			yield return Toils_Goto.MoveOffTargetBlueprint(TargetIndex.B);
			yield return Toils_Construct.MakeSolidThingFromBlueprintIfNecessary(TargetIndex.B, TargetIndex.C);
			yield return Toils_Haul.DepositHauledThingInContainer(TargetIndex.B, TargetIndex.C);
			yield return Toils_Haul.JumpToCarryToNextContainerIfPossible(carryToContainer, TargetIndex.C);
		}

        protected Toil DetermineNumToHaul()
        {
            return new Toil
            {
                initAction = delegate
                {
                    int num = BillyLoadTransporterUtility.CountLeftToTransfer(this.pawn, this.Transferable, this.Transporter.TryGetComp<CompTransporter>());
                    if (this.pawn.carryTracker.CarriedThing != null)
                    {
                        num -= this.pawn.carryTracker.CarriedThing.stackCount;
                    }
                    if (num <= 0)
                    {
                        this.pawn.jobs.EndCurrentJob(JobCondition.Succeeded, true);
                    }
                    else
                    {
                        base.CurJob.count = num;
                    }
                },
                defaultCompleteMode = ToilCompleteMode.Instant,
                atomicWithPrevious = true
            };
        }

        private Toil AddCarriedThingToTransferables()
        {
            return new Toil
            {
                initAction = delegate
                {
                    TransferableOneWay transferable = this.Transferable;
                    if (!transferable.things.Contains(this.pawn.carryTracker.CarriedThing))
                    {
                        transferable.things.Add(this.pawn.carryTracker.CarriedThing);
                    }
                },
                defaultCompleteMode = ToilCompleteMode.Instant,
                atomicWithPrevious = true
            };
        }
    }

    public class JobGiver_BillyLoadTransporters : ThinkNode_JobGiver
    {
        private static List<CompTransporter> tmpTransporters = new List<CompTransporter>();

        protected override Job TryGiveJob(Pawn pawn)
        {
            int transportersGroup = pawn.mindState.duty.transportersGroup;
            TransporterUtility.GetTransportersInGroup(transportersGroup, pawn.Map, tmpTransporters);
            for (int i = 0; i < tmpTransporters.Count; i++)
            {
                CompTransporter transporter = tmpTransporters[i];
                if (BillyLoadTransporterUtility.HasJobOnTransporter(pawn, transporter))
                {
                    return BillyLoadTransporterUtility.JobOnTransporter(pawn, transporter);
                }
            }
            return null;
        }
    }

    public class WorkGiver_BillyLoadTransporters : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest
        {
            get
            {
                return ThingRequest.ForGroup(ThingRequestGroup.Transporter);
            }
        }

        public override PathEndMode PathEndMode
        {
            get
            {
                return PathEndMode.Touch;
            }
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            CompTransporter transporter = t.TryGetComp<CompTransporter>();
            return BillyLoadTransporterUtility.HasJobOnTransporter(pawn, transporter);
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            CompTransporter transporter = t.TryGetComp<CompTransporter>();
            return BillyLoadTransporterUtility.JobOnTransporter(pawn, transporter);
        }
    }
}
