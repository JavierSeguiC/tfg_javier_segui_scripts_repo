function [loops, t] = build_loop_signals(rec, opt)
%BUILD_LOOP_SIGNALS  Assemble the 5 PI-loop signal structs from a loaded recording.
%
%   [loops, t] = BUILD_LOOP_SIGNALS(rec, opt) takes rec as returned by
%   LOAD_RECORDING (must include the 'control' stream; 'notes' and 'meta'
%   used if present) and returns:
%     t     : session-clock time vector (s), from rec.control.t_session
%     loops : 1x1 (ReflexOnly) or 1x5 struct array, one per PI loop, with
%             fields y, u, r, name, yLabel, uLabel, yUnit, uKind, evT,
%             evGreen -- everything evaluate_control_loops.m /
%             player_ability_summary.m / aggregate_loop_metrics.m need, so
%             none of them re-derive setpoints, event classification, or
%             the force-margin placeholder trim independently.
%
%   opt fields used (all optional; sensible defaults applied):
%     ReflexOnly (false)  build only loop 1
%     RefReflex  (10)     reflex setpoint fallback, used only if
%                         rec.rReflex (from sessionMeta) is not finite
%     RefForce   (0.05)   force-margin setpoint fallback, used only if
%                         rec.rForce (from sessionMeta) is not finite
%     TimeAlign  ('auto') 'auto'|'raw'|'sharedZero' -- how to align
%                         note-event timestamps to the control clock, ONLY
%                         used as a fallback for recordings whose
%                         noteOutcomes lacks a precomputed t_session
%                         (current SessionRecorder always writes one)
%
%   Event relevance per loop mirrors the controller's own estimator gating:
%     Reflex : GREEN = correct-timing presses {Hit, ForceInsufficient, UnderHeld}
%              RED   = {Missed, EarlyPress, LatePress}   (== the e_dot classifier)
%     Force l: GREEN = {Hit}                              on lane l (baseline sample)
%              RED   = {ForceInsufficient, UnderHeld}     on lane l (force failure mode)
%              (timing failures / wrong-lane / missed excluded, as they do NOT
%               update M_F,l)
%
%   Requires base MATLAB only. R2024b baseline.

if ~isfield(opt,'ReflexOnly') || isempty(opt.ReflexOnly), opt.ReflexOnly = false; end
if ~isfield(opt,'RefReflex')  || isempty(opt.RefReflex),  opt.RefReflex  = 10;   end
if ~isfield(opt,'RefForce')   || isempty(opt.RefForce),   opt.RefForce   = 0.05; end
if ~isfield(opt,'TimeAlign')  || isempty(opt.TimeAlign),  opt.TimeAlign  = 'auto'; end
if isscalar(opt.RefForce), opt.RefForce = repmat(opt.RefForce,1,4); end

if isempty(rec.control)
    error('build_loop_signals:noControl', 'rec.control is empty -- load with Streams including ''control''.');
end
C = rec.control;

t = tcol(C, {'t_session','t','time'});
t = double(t(:));
t0c = min(t);

evt = buildEvents(rec, t0c, opt.TimeAlign);

% ---- Per-session setpoints (sessionMeta, authoritative) override the
% opt fallback when present -- SessionRecorder logs these (Aug 2026+).
rReflex = opt.RefReflex;
if isfield(rec,'rReflex') && isfinite(rec.rReflex), rReflex = rec.rReflex; end
rForce = opt.RefForce;
if isfield(rec,'rForce') && isfinite(rec.rForce), rForce = repmat(rec.rForce,1,4); end

reflexGreen = {'hit','forceinsufficient','underheld'};
reflexRed   = {'missed','earlypress','latepress'};
forceGreen  = {'hit'};
forceRed    = {'forceinsufficient','underheld'};

loops = struct('name',{},'y',{},'u',{},'r',{}, ...
               'yLabel',{},'uLabel',{},'yUnit',{}, ...
               'evT',{},'evGreen',{},'uKind',{});

% ----- Loop 1: reflex ---------------------------------------------------
y = tcol(C, {'e_dot','edot','e_rate'});
u = tcol(C, {'d'});
if all(isnan(u))  % non-PI session may log d as NaN; fall back to v for context
    u = tcol(C, {'v'});  uLabel = 'v (note speed)'; uKind = 'v';
else
    uLabel = 'd (reflex command)'; uKind = 'd';
end
[eT, eG] = selectEvents(evt, reflexGreen, reflexRed, []);   % [] = any lane
loops(1) = struct('name','Loop 1 — Reflex (ė)', ...
    'y',y, 'u',u, 'r',rReflex, ...
    'yLabel','ė  [err/min]', 'uLabel',uLabel, 'yUnit','err/min', ...
    'evT',eT, 'evGreen',eG, 'uKind',uKind);

% ----- Loops 2-5: force per lane (lane index is already CANONICAL/finger-
% indexed here, per load_recording.m's handedness remap) -----------------
if ~opt.ReflexOnly
    FINGER_NAMES = {'Index','Middle','Ring','Pinky'};
    for lane = 0:3
        finger = FINGER_NAMES{lane+1};
        y = tcol(C, {sprintf('M_F%d',lane), sprintf('MF%d',lane)});
        y = trimForcePlaceholder(y);
        u = tcol(C, {sprintf('tau%d',lane)});
        [eT, eG] = selectEvents(evt, forceGreen, forceRed, lane);
        loops(end+1) = struct( ...
            'name', sprintf('Loop %d — Force %s (M_{F,%s})', lane+2, finger, finger), ...
            'y', y, 'u', u, 'r', rForce(lane+1), ...
            'yLabel', sprintf('M_{F,%s}',finger), 'uLabel', sprintf('\\tau_{%s}',finger), ...
            'yUnit','force margin', 'evT', eT, 'evGreen', eG, 'uKind','tau'); %#ok<AGROW>
        if all(isnan(y))
            warning('build_loop_signals:noForceData', ...
                'Force lane %d (%s) has no measured data (M_F all NaN) — force loops may have been disabled for this session.', lane, finger);
        end
    end
end
end % ===================== end main =====================================


% ========================================================================
function y = trimForcePlaceholder(y)
% Force-margin columns start at a PLACEHOLDER, not a real measurement: the
% controller seeds the readout at a fixed floor value (0 - maxTau) until the
% lane's first real force sample arrives (older recordings, pre-fix,
% instead logged NaN for that same prefix). Either way that leading run
% carries no information and shouldn't be plotted or fed into the
% stabilization/bias/trend statistics. Set it to NaN so it's excluded
% exactly like any other missing sample (plot: gap; metrics: isfinite filter).
%
% The two styles need different trimming: a NaN run is followed immediately
% by the first REAL sample (keep it); a flat-seed run (no NaN at all, new
% controller) repeats the seed value verbatim until the first real sample
% overwrites it (trim the whole repeated run, keep only what's after it).
if isempty(y), return; end
n = numel(y);
i = 1;
while i <= n && isnan(y(i)), i = i + 1; end   % leading NaN run, old-style
if i > n, return; end                          % all NaN: nothing to trim
if i > 1
    y(1:i-1) = NaN;                            % trim exactly the NaNs; y(i) is real, keep it
    return;
end
% No leading NaN: check for a flat seed run starting at sample 1 (new-style).
v0 = y(1);
j = 1;
while j+1 <= n && abs(y(j+1) - v0) < 1e-6
    j = j + 1;
end
if j > 1
    y(1:j) = NaN;                              % whole repeated seed run was placeholder
end
end


% ========================================================================
function [eT, isGreen] = selectEvents(evt, greenSet, redSet, lane)
% Return event times and a logical green/red flag for the relevant outcomes.
if isempty(evt.t)
    eT = []; isGreen = logical([]); return;
end
% Work on a single common length so no mask can overflow another vector.
n  = min([numel(evt.t), numel(evt.outcome), numel(evt.lane), numel(evt.correctLane)]);
tt = evt.t(1:n);
oc = lower(string(evt.outcome(1:n)));
keepLane = true(n,1);
if ~isempty(lane)
    keepLane = (evt.lane(1:n) == lane) & evt.correctLane(1:n);  % right finger only
end
isG  = ismember(oc, string(greenSet)) & keepLane;
isR  = ismember(oc, string(redSet))   & keepLane;
keep = isG | isR;
eT      = tt(keep);
isGreen = isG(keep);
end


% ========================================================================
function evt = buildEvents(rec, t0ctrl, alignMode)
% Build the event struct {.t .outcome .lane .correctLane} from rec.notes
% (already loaded/typed by load_recording -- correctLane etc. are already
% logical there). Falls back to guessed alignment only for recordings whose
% noteOutcomes lacks a precomputed t_session.
evt = struct('t',[],'outcome',{{}},'lane',[],'correctLane',logical([]));
if isempty(rec.notes), return; end
N = rec.notes;

tS = tcol(N, {'t_session'});
if ~isempty(tS) && any(isfinite(tS))
    tE = tS; haveSessionClock = true;
else
    tE = tcol(N, {'tEnter','t_enter','tenter','tarrival'});
    if isempty(tE) || all(isnan(tE))
        tE = tcol(N, {'tExit','t_exit'});
    end
    haveSessionClock = false;
end

oc = ttext(N, {'outcome','Outcome'});
lane = tcol(N, {'lane','Lane'});
cl = tbool(N, {'correctLane','correct_lane'});

if isempty(tE) || isempty(oc)
    return;
end

tE = tE(:); oc = oc(:);
if isempty(lane), lane = nan(size(tE)); else, lane = lane(:); end
if isempty(cl),   cl   = true(size(tE)); else, cl = logical(cl(:)); end
n = min([numel(tE), numel(oc), numel(lane), numel(cl)]);
tE = tE(1:n); oc = oc(1:n); lane = lane(1:n); cl = cl(1:n);

if haveSessionClock
    shift = 0;
else
    switch lower(alignMode)
        case 'raw'
            shift = 0;
        case 'sharedzero'
            shift = -min(tE);
        otherwise % 'auto'
            offset = min(tE) - t0ctrl;
            if abs(offset) > 5
                shift = -min(tE) + t0ctrl;
                warning('build_loop_signals:timeAlign', ...
                    'Note-event clock offset ~%.1fs from control clock; auto-aligned. Use ''TimeAlign'',''raw'' to disable.', offset);
            else
                shift = 0;
            end
    end
end

evt.t = tE + shift;
evt.outcome = oc;
evt.lane = lane;
evt.correctLane = cl;
end


% ========================================================================
function v = tcol(T, names)
% First matching column of table T as a numeric column vector, or [].
% Case-insensitive; tolerant of the column being string/cell text.
v = [];
if isempty(T), return; end
vn = T.Properties.VariableNames;
for k = 1:numel(names)
    idx = find(strcmpi(vn, names{k}), 1);
    if ~isempty(idx)
        col = T{:, idx};
        if iscell(col) || isstring(col), col = str2double(string(col)); end
        v = double(col(:));
        return;
    end
end
end


% ========================================================================
function s = ttext(T, names)
% First matching column of table T as an N-by-1 string array, or [].
s = [];
if isempty(T), return; end
vn = T.Properties.VariableNames;
for k = 1:numel(names)
    idx = find(strcmpi(vn, names{k}), 1);
    if ~isempty(idx)
        s = string(T{:, idx});
        s = s(:);
        return;
    end
end
end


% ========================================================================
function b = tbool(T, names)
% First matching column of table T as an N-by-1 logical, or []. Handles the
% column already being logical (typical: load_recording normalizes Unity
% True/False text columns already) as well as numeric 0/1 or leftover text.
b = [];
if isempty(T), return; end
vn = T.Properties.VariableNames;
for k = 1:numel(names)
    idx = find(strcmpi(vn, names{k}), 1);
    if ~isempty(idx)
        col = T{:, idx};
        if islogical(col)
            b = col(:);
        elseif isnumeric(col)
            b = col(:) ~= 0;
        else
            str = lower(strtrim(string(col)));
            b = ismember(str, ["true","1","yes","y"]);
            b = b(:);
        end
        return;
    end
end
end
