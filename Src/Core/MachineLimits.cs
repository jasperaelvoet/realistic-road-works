using Unity.Mathematics;

namespace RealisticRoadWorks.V3
{
    // Realistic speed / acceleration limits per crew role, in MACHINE seconds (machine clock = sim
    // clock / 60, the same clock every leg is timed in). Pure data + pure kinematics helpers, job-safe, no allocation.
    //
    // Contract (Machines):
    //   * NO leg of any kind (Drive, Reverse, Turn, Follow/creep, DigHop, Shuttle, Spread, Scrape, Pave, Paint, DriveOut, catch-up
    //     after a phase / front jump, respawn approach, guard back-out, post-load placement drive, leave) may move a puppet faster
    //     than VFwd forward / VRev reversing / VTurn on K-turn arcs, or change its speed faster than Accel. A planned leg that would
    //     need more is STRETCHED in time (its end time moves later and every following leg is re-timed) and counted as a clamped leg.
    //   * Front-relative legs (Follow / creep with F) never chase the front faster than the limit of the leg: VWork for the front
    //     owner's working activity when the crew layout reaches it (PhasePlan.CrewCountFor sizes crews so it does), otherwise
    //     at most VFwd (counted "overWork"); a front faster than VFwd is NOT chased: the puppet lags (re-timed, counted "frontLag").
    //   * Leavers (DriveOut, post-site, release) use VLeave instead of VFwd (a paver / painter driving off in transport mode).
    //   * Only the separation guard's emergency brake may decelerate harder than Accel (up to RRWConst.kEmergencyDecel).
    public struct MachineLimits
    {
        public float VFwd;    // m/s, forward, any on-site leg
        public float VRev;    // m/s, reversing
        public float VTurn;   // m/s, on K-turn arcs and the reverse half of a turn (min(VTurn, VRev) when reversing)
        public float Accel;   // m/s^2, |dv/dt| of planned legs (speeding up AND braking)
        public float VWork;   // m/s, front advance of this role's WORKING activity (crew sizing; 0 = the role never owns a front)
        public float VLeave;  // m/s, forward speed while leaving (DriveOut / post-site / release)

        public float VFor(bool reverse, bool turning, bool leaving)
        {
            float v = reverse ? VRev : leaving ? math.max(VFwd, VLeave) : VFwd;
            return turning ? math.min(v, reverse ? math.min(VTurn, VRev) : VTurn) : v;
        }

        // ---------------------------------------------------------------- the table

        // Limits of a role in a phase. Finisher: C3 = paver (0.05-0.15 m/s working, 1 m/s repositioning), C4 = line painter
        // (1.5 m/s). Loader = the grader (C0 topsoil scrape, C2 / D2 spread). Unknown role: the slowest table row (excavator).
        public static MachineLimits Of(MachineRole role, WorksPhase ph)
        {
            switch (role)
            {
                case MachineRole.Excavator:     // tracked MiningExcavator01 clone at 0.4 (Dig / Break hop with the front)
                    return new MachineLimits { VFwd = 1.2f, VRev = 0.8f, VTurn = 0.6f, Accel = 0.5f, VWork = 0.30f, VLeave = 1.2f };
                case MachineRole.Loader:        // grader / loader
                    return new MachineLimits { VFwd = 2.0f, VRev = 1.5f, VTurn = 1.0f, Accel = 0.5f,
                                               VWork = ph == WorksPhase.Survey ? 1.0f : 0.5f, VLeave = 2.0f };
                case MachineRole.TruckA:
                case MachineRole.TruckB:        // CoalTruck01 dump trucks
                    return new MachineLimits { VFwd = 5.5f, VRev = 2.0f, VTurn = 2.0f, Accel = 1.5f, VWork = 0f, VLeave = 5.5f };
                case MachineRole.CrewTruck:     // RoadMaintenanceVehicle01 crew truck
                    return new MachineLimits { VFwd = 6.0f, VRev = 2.0f, VTurn = 2.0f, Accel = 1.5f, VWork = 0f, VLeave = 6.0f };
                case MachineRole.Finisher:
                    if (ph == WorksPhase.Paving)  // paver proxy
                        return new MachineLimits { VFwd = 1.0f, VRev = 0.5f, VTurn = 0.5f, Accel = 0.3f, VWork = 0.15f, VLeave = 1.0f };
                    // line painter (C4) and any other phase; drives off in transport mode at VLeave
                    return new MachineLimits { VFwd = 1.5f, VRev = 1.0f, VTurn = 1.0f, Accel = 0.5f, VWork = 1.5f, VLeave = 4.0f };
                case MachineRole.Roller:        // Tandem roller (GameObject unit, not a puppet). Symmetric machine: reverses as
                                                // fast as it drives; compaction passes at RRWConst.kRollerSpeed* (1.0-1.5 m/s, <= 0.92 x VFwd);
                                                // 0.4 m/s^2 (verified in game). Never owns a front (VWork 0): it never sizes crews.
                    return new MachineLimits { VFwd = 2.0f, VRev = 2.0f, VTurn = 1.0f, Accel = 0.4f, VWork = 0f, VLeave = 2.0f };
                default:
                    return new MachineLimits { VFwd = 1.2f, VRev = 0.8f, VTurn = 0.6f, Accel = 0.5f, VWork = 0.30f, VLeave = 1.2f };
            }
        }

        // Working front speed of the phase's FRONT OWNER (CrewPlan.FrontOwner): the speed PhasePlan.CrewCountFor sizes crews for.
        public static float WorkSpeed(WorksKind kind, WorksPhase ph)
        {
            switch (ph)
            {
                case WorksPhase.Survey: return Of(MachineRole.Loader, ph).VWork;        // scrape 1.0
                case WorksPhase.Excavation: return Of(MachineRole.Excavator, ph).VWork; // dig 0.30
                case WorksPhase.Foundation: return Of(MachineRole.Loader, ph).VWork;    // spread 0.5
                case WorksPhase.Paving: return Of(MachineRole.Finisher, ph).VWork;      // pave 0.15
                case WorksPhase.Finishing: return Of(MachineRole.Finisher, ph).VWork;   // paint 1.5
                case WorksPhase.BreakUp:
                case WorksPhase.Removal: return Of(MachineRole.Excavator, ph).VWork;    // break / dig 0.30
                case WorksPhase.Restore: return Of(MachineRole.Loader, ph).VWork;       // spread 0.5
                default: return 0f;
            }
        }

        // ---------------------------------------------------------------- kinematics (rest-to-rest trapezoid / triangle)

        // Shortest time to travel `dist` metres starting and ending at rest with top speed vmax and |a| <= accel.
        public static float MinLegSeconds(float dist, float vmax, float accel)
        {
            dist = math.abs(dist);
            if (dist <= 1e-5f) return 0f;
            if (vmax <= 1e-4f || accel <= 1e-4f) return float.PositiveInfinity;
            float dAcc = vmax * vmax / accel;              // accelerate to vmax and brake back to 0
            if (dist >= dAcc) return dist / vmax + vmax / accel;
            return 2f * math.sqrt(dist / accel);           // triangle profile
        }

        // Farthest rest-to-rest distance within `seconds`.
        public static float MaxReach(float seconds, float vmax, float accel)
        {
            if (seconds <= 0f || vmax <= 0f || accel <= 0f) return 0f;
            float tAcc = vmax / accel;
            if (seconds >= 2f * tAcc) return vmax * (seconds - tAcc);
            float h = 0.5f * seconds;
            return accel * h * h;
        }

        // True when a rest-to-rest leg of `dist` metres fits in `seconds` (with a 1 ms tolerance).
        public static bool Feasible(float dist, float seconds, float vmax, float accel) =>
            MinLegSeconds(dist, vmax, accel) <= seconds + 1e-3f;

        // Duration a planned leg must get: the planned duration, stretched to the minimum the limits allow. `stretched` is set when
        // the leg had to be stretched (Machines counts these: rrw.mx.check "clampedLegs").
        public static float Stretch(float dist, float plannedSeconds, float vmax, float accel, out bool stretched)
        {
            float tmin = MinLegSeconds(dist, vmax, accel);
            stretched = tmin > plannedSeconds + 1e-3f;
            return stretched ? tmin : plannedSeconds;
        }

        // rrw.mx.check / rrw.mx.trace: an observed speed / acceleration over its limit (with the RRWConst tolerances).
        public static bool SpeedViolates(float observed, float limit) =>
            math.abs(observed) > limit * (1f + RRWConst.kSpeedCheckTolerance) + 0.05f;
        public static bool AccelViolates(float observed, float limit) =>
            math.abs(observed) > limit * (1f + RRWConst.kAccelCheckTolerance) + 0.1f;
    }
}
