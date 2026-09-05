function out = force_capability_profile(root, varargin)
%FORCE_CAPABILITY_PROFILE  Reconstructed per-finger force profiles, Tap vs Hold.
%
%   out = FORCE_CAPABILITY_PROFILE(root) produces ONE FIGURE PER PLAYER, a
%   4x2 grid (rows = lane/finger 0-3, columns = Tap, Hold), each cell an
%   empirical force-over-time profile reconstructed from the RAW sensor
%   stream (rawInputs_*.csv, ~60 Hz) for that player's notes on that lane
%   -- not a fitted/synthetic shape. Plus ONE POPULATION FIGURE, same
%   layout, pooling every player's notes.
%
%   WHY RAW DATA, NOT FITTED PARAMETERS: noteOutcomes only logs 3 summary
%   numbers per hold (fMax, fAvg, fSustained80). Fitting a parametric shape
%   (e.g. trapezoidal) from 3 numbers is underdetermined -- very different
%   real force traces can share the same 3 summary numbers, so a "fit"
%   would be inventing an attack/decay shape, not measuring one. Instead,
%   each note's REAL press window is located and the REAL rawInputs force
%   trace is sliced directly: exact, not reconstructed from summaries.
%
%   WHY RAW FORCE, NOT MARGIN: margin (appliedForce - requiredForce)
%   confounds finger strength with whatever threshold the controller
%   happened to set that session. Raw force is the actual physical
%   quantity, independent of tau.
%
%   NOTE-TO-PRESS MATCHING: noteOutcomes doesn't cross-reference
%   inputProfiles, so each note's physical press window is found by
%   SAME-LANE nearest-time matching (press.t_session closest to
%   note.t_session, within MatchSlack) -- much less ambiguous than
%   cross-lane matching (see lane_confusion_matrix.m) since there's no
%   "which member" question, only "which press, if any, resolved this
%   note". onset = press.t_session; release = press.t_session + duration
%   (both already on the shared session clock). Notes with no matching
%   press are skipped (counted in .attribution).
%
%   NOTES USED: correctLane==true and outcome ~= "Missed" (so Hit,
%   ForceInsufficient, UnderHeld, EarlyPress, LatePress all count --
%   timing correctness isn't relevant to force CAPABILITY). noteType in
%   {Tap,Strength} -> "Tap" category (matches NoteResolver's own fMax-
%   as-reference convention); {Hold} -> "Hold" category.
%
%   PER-NOTE METRICS (computed on that note's own REAL segment, then
%   aggregated as median/Q1/Q3 across a player's notes -- NOT fit on a
%   single pooled/omnibus curve, for the same reason every other script in
%   this pipeline aggregates per-unit-then-summarises rather than pooling
%   raw autocorrelated samples):
%     peak         max force in [onset-PadPre, release+PadPost]
%     timeToPeak   time of that peak, relative to onset
%     (Hold only)
%     plateauTrend OLS slope of force during the PLATEAU sub-window
%                  ([PlateauStartFrac, PlateauEndFrac] x that note's own
%                  [onset,release] span), force units per SECOND. Sign and
%                  magnitude only -- no fatigue/learning interpretation is
%                  attached here, that's left to you.
%     jitter       std of the DETRENDED residual in the same plateau
%                  window (the trend removed first, so jitter isn't
%                  inflated by a real slope).
%   "Release-drop" timing (time from release cue to dropping below
%   threshold) is deliberately NOT computed.
%
%   PROFILE CURVE (the plotted band): each qualifying note's real segment
%   is resampled onto a common per-(player,lane,category) grid (aligned at
%   onset, t=0), truncated to the SHORTEST such segment so every point in
%   the band is backed by the full note count, then reduced to a pointwise
%   median + IQR -- same truncation convention as compare_controllers_
%   difficulty_trace.m / cross_player_convergence.m.
%
%   No controller filtering (all sessions) -- like lane_confusion_matrix.m,
%   this is a player physical-capability question, not a controller one.
%
%   out = FORCE_CAPABILITY_PROFILE(root, Name, Value, ...) accepts:
%     PadPre    (0.20)  s of context shown before the matched press onset.
%     PadPost   (0.30)  s of context shown after the matched press release.
%     MatchSlack(0.5)   s search radius for the same-lane press match.
%     PlateauStartFrac (0.25)  start of the Hold plateau sub-window, as a
%     PlateauEndFrac   (0.85)  fraction of that note's own [onset,release] span.
%     GridDt    (1/60)  s, resampling step for the profile curve (matches
%                       rawInputs' own ~60 Hz rate).
%     MinNotes  (5)     minimum notes required to plot/report a
%                       (player|population, lane, category) cell.
%     Plot      (true)  draw both figures.
%     Quiet     (true)  suppress per-folder missing-file warnings.
%
%   RETURNS out, a struct with:
%     .perPlayer    struct array, one row per (player, lane, category):
%                   profileId, lane, category, nNotes, peakMedian/Q1/Q3,
%                   ttpMedian/Q1/Q3, plateauTrendMedian/Q1/Q3 (NaN for
%                   Tap), jitterMedian/Q1/Q3 (NaN for Tap), grid,
%                   curveMedian, curveQ1, curveQ3.
%     .population   same shape, pooled across all players (profileId =
%                   "POPULATION").
%     .attribution  table: folder, profileId, nCandidateNotes, nMatched,
%                   nUnmatched -- press-matching coverage, for transparency.
%
%   Requires: load_recording.m, build_session_index.m on the path. Base
%   MATLAB only otherwise. R2024b baseline.

p = inputParser;
p.addParameter('PadPre',    0.20,  @(x)isnumeric(x)&&isscalar(x)&&x>=0);
p.addParameter('PadPost',   0.30,  @(x)isnumeric(x)&&isscalar(x)&&x>=0);
p.addParameter('MatchSlack',0.5,   @(x)isnumeric(x)&&isscalar(x)&&x>=0);
p.addParameter('PlateauStartFrac', 0.25, @(x)isnumeric(x)&&isscalar(x)&&x>=0&&x<1);
p.addParameter('PlateauEndFrac',   0.85, @(x)isnumeric(x)&&isscalar(x)&&x>0&&x<=1);
p.addParameter('GridDt',    1/60,  @(x)isnumeric(x)&&isscalar(x)&&x>0);
p.addParameter('MinNotes',  5,     @(x)isnumeric(x)&&isscalar(x)&&x>=1);
p.addParameter('Plot',      true,  @(x)islogical(x)||isnumeric(x));
p.addParameter('Quiet',     true,  @(x)islogical(x)||isnumeric(x));
p.parse(varargin{:});
opt = p.Results;
opt.Plot  = logical(opt.Plot);
opt.Quiet = logical(opt.Quiet);

idx = build_session_index(root, 'HeavyStats', false, 'Quiet', opt.Quiet);
if height(idx) == 0
    warning('force_capability_profile:noSessions', 'No sessions found under %s', root);
    out = emptyOut(); return;
end
fprintf('Reconstructing force profiles over %d session(s) (all controllers)...\n', height(idx));

categories = {'Tap','Hold'};
[players, ~, playerGroups] = unique(idx.profileId, 'stable');
numPlayers = numel(players);

emptyBucket = @() struct('trel',{},'f',{},'peak',{},'ttp',{}, ...
                          'plateauTrend',{},'jitter',{});
globalBucket = cell(4,2);
for l=1:4, for c=1:2, globalBucket{l,c} = emptyBucket(); end, end

attribRows = cell(0,5);
perPlayer = struct('profileId',{},'lane',{},'category',{},'nNotes',{}, ...
    'peakMedian',{},'peakQ1',{},'peakQ3',{}, 'ttpMedian',{},'ttpQ1',{},'ttpQ3',{}, ...
    'plateauTrendMedian',{},'plateauTrendQ1',{},'plateauTrendQ3',{}, ...
    'jitterMedian',{},'jitterQ1',{},'jitterQ3',{}, ...
    'grid',{},'curveMedian',{},'curveQ1',{},'curveQ3',{});

for pIdx = 1:numPlayers
    pName = players(pIdx);
    if ismissing(pName) || pName == "", pName = "Unknown"; end
    pSessions = idx(playerGroups == pIdx, :);

    playerBucket = cell(4,2);
    for l=1:4, for c=1:2, playerBucket{l,c} = emptyBucket(); end, end

    for s = 1:height(pSessions)
        folder = char(pSessions.folder(s));
        try
            rec = load_recording(folder, 'Streams', {'meta','notes','presses','raw'}, 'Quiet', opt.Quiet);
        catch ME
            warning('force_capability_profile:skip', 'Skipping %s: %s', folder, ME.message);
            continue;
        end
        if isempty(rec.notes) || isempty(rec.presses) || isempty(rec.raw)
            continue;
        end

        [playerBucket, globalBucket, nCand, nMatched] = processSession(rec, opt, playerBucket, globalBucket);
        attribRows(end+1,:) = { string(folder), string(pName), nCand, nMatched, nCand-nMatched }; %#ok<AGROW>
    end

    for l = 0:3
        for c = 1:2
            rec_ = playerBucket{l+1,c};
            if numel(rec_) < opt.MinNotes, continue; end
            entry = summariseBucket(rec_, opt);
            entry.profileId = pName; entry.lane = l; entry.category = string(categories{c});
            perPlayer(end+1) = entry; %#ok<AGROW>
        end
    end
end

attributionT = cell2table(attribRows, 'VariableNames', ...
    {'folder','profileId','nCandidateNotes','nMatched','nUnmatched'});
fprintf('Press-matching coverage: %d/%d candidate notes matched to a physical press.\n', ...
    sum(attributionT.nMatched), sum(attributionT.nCandidateNotes));

population = struct('profileId',{},'lane',{},'category',{},'nNotes',{}, ...
    'peakMedian',{},'peakQ1',{},'peakQ3',{}, 'ttpMedian',{},'ttpQ1',{},'ttpQ3',{}, ...
    'plateauTrendMedian',{},'plateauTrendQ1',{},'plateauTrendQ3',{}, ...
    'jitterMedian',{},'jitterQ1',{},'jitterQ3',{}, ...
    'grid',{},'curveMedian',{},'curveQ1',{},'curveQ3',{});
for l = 0:3
    for c = 1:2
        rec_ = globalBucket{l+1,c};
        if numel(rec_) < opt.MinNotes, continue; end
        entry = summariseBucket(rec_, opt);
        entry.profileId = "POPULATION"; entry.lane = l; entry.category = string(categories{c});
        population(end+1) = entry; %#ok<AGROW>
    end
end

if opt.Plot
    for pIdx = 1:numel(players)
        pName = players(pIdx);
        if ismissing(pName) || pName == "", pName = "Unknown"; end
        sel = perPlayer([perPlayer.profileId] == pName);
        if isempty(sel), continue; end
        dName = idx.name_(find(idx.profileId == pName, 1));
        if ismissing(dName) || dName == "", dName = pName; end
        drawProfileGrid(sel, categories, sprintf('Force capability — %s', dName));
    end
    if ~isempty(population)
        drawProfileGrid(population, categories, 'Force capability — POPULATION (all players)');
    end
end

out.perPlayer   = perPlayer;
out.population  = population;
out.attribution = attributionT;
end % ===================== end main =====================================


% ========================================================================
function [playerBucket, globalBucket, nCand, nMatched] = processSession(rec, opt, playerBucket, globalBucket)
% One session's contribution: match each qualifying note to a press,
% slice its real force segment, compute per-note metrics, and append into
% BOTH the per-player and the global (population) buckets.
N = rec.notes;
need = {'lane','outcome','correctLane','t_session','noteType'};
if ~all(ismember(need, N.Properties.VariableNames))
    nCand = 0; nMatched = 0; return;
end

trueLane = double(N.lane);
outcome  = lower(string(N.outcome));
correctLane = logical(N.correctLane);
tNote = double(N.t_session);
noteType = lower(string(N.noteType));

catOf = strings(height(N),1);
catOf(ismember(noteType, ["tap","strength"])) = "Tap";
catOf(noteType == "hold") = "Hold";

candMask = correctLane & (outcome ~= "missed") & (catOf ~= "") & ...
           isfinite(trueLane) & trueLane>=0 & trueLane<=3 & isfinite(tNote);
candIdx = find(candMask);
nCand = numel(candIdx);
nMatched = 0;

P = rec.presses;
haveP = all(ismember({'t_session','lane','duration'}, P.Properties.VariableNames));
if haveP
    pLane = double(P.lane); pT = double(P.t_session); pDur = double(P.duration);
end

R = rec.raw;
haveRaw = ismember('t_session', R.Properties.VariableNames);
if ~haveRaw, nMatched = 0; return; end
rawT = double(R.t_session);

for k = 1:numel(candIdx)
    i = candIdx(k);
    if ~haveP, continue; end
    l = trueLane(i);
    cand = find(pLane == l & abs(pT - tNote(i)) <= opt.MatchSlack);
    if isempty(cand), continue; end
    [~, best] = min(abs(pT(cand) - tNote(i)));
    press = cand(best);
    onset = pT(press); releaseT = onset + pDur(press);
    if ~isfinite(onset) || ~isfinite(releaseT) || releaseT < onset, continue; end

    fcol = tcol(R, {sprintf('f_lane%d', l)});
    if isempty(fcol), continue; end
    winLo = onset - opt.PadPre; winHi = releaseT + opt.PadPost;
    inWin = rawT >= winLo & rawT <= winHi & isfinite(fcol);
    if nnz(inWin) < 3, continue; end
    trel = rawT(inWin) - onset;
    fseg = fcol(inWin);
    % Defend against duplicate/non-monotonic timestamps in rawInputs (seen
    % around pause/resume boundaries, and in some older-schema recordings)
    % -- interp1 requires strictly unique sample points, and duplicates
    % here would otherwise silently vary by which session happened to hit
    % this edge case. Same defensive pattern used for the control stream
    % in build_loop_signals.m.
    [trel, ord] = sort(trel); fseg = fseg(ord);
    [trel, iu] = unique(trel, 'stable'); fseg = fseg(iu);
    if numel(trel) < 3, continue; end

    releaseRel = releaseT - onset;
    cat = catOf(i);
    [peak, ttp, plateauTrend, jitter] = perNoteMetrics(trel, fseg, releaseRel, cat, opt);

    rec_ = struct('trel',trel, 'f',fseg, 'peak',peak, 'ttp',ttp, ...
                   'plateauTrend',plateauTrend, 'jitter',jitter);
    cIdx = find(cat == ["Tap","Hold"]);
    playerBucket{l+1, cIdx}(end+1) = rec_;
    globalBucket{l+1, cIdx}(end+1) = rec_;
    nMatched = nMatched + 1;
end
end


% ========================================================================
function [peak, ttp, plateauTrend, jitter] = perNoteMetrics(trel, f, releaseRel, cat, opt)
peak = max(f);
iPk = find(f == peak, 1, 'first');
ttp = trel(iPk);
plateauTrend = NaN; jitter = NaN;

if cat == "Hold" && releaseRel > 0
    lo = opt.PlateauStartFrac * releaseRel;
    hi = opt.PlateauEndFrac   * releaseRel;
    pm = trel >= lo & trel <= hi;
    if nnz(pm) >= 5
        tp = trel(pm); fp = f(pm);
        slope = olsSlopeSimple(tp, fp);
        plateauTrend = slope;
        if isfinite(slope)
            intercept = mean(fp) - slope*mean(tp);
            resid = fp - (intercept + slope*tp);
            jitter = std(resid, 0);
        end
    end
end
end


% ========================================================================
function slope = olsSlopeSimple(t, x)
t = t(:); x = x(:);
tc = t - mean(t);
Stt = sum(tc.^2);
if Stt <= 0, slope = NaN; return; end
slope = sum(tc .* (x - mean(x))) / Stt;
end


% ========================================================================
function entry = summariseBucket(rec_, opt)
% One (player|population, lane, category) summary: per-note metric
% quantiles, plus the resampled median/IQR profile curve.
n = numel(rec_);
peakQ = quantileNT([rec_.peak]);
ttpQ  = quantileNT([rec_.ttp]);
ptQ   = quantileNT([rec_.plateauTrend]);
jitQ  = quantileNT([rec_.jitter]);

Ttrunc = min(arrayfun(@(r) r.trel(end), rec_));
Tstart = max(arrayfun(@(r) r.trel(1), rec_));
tGrid = (Tstart:opt.GridDt:Ttrunc)';
if numel(tGrid) < 3
    tGrid = linspace(Tstart, Ttrunc, 3)';
end
M = nan(numel(tGrid), n);
for i = 1:n
    M(:,i) = interp1(rec_(i).trel, rec_(i).f, tGrid, 'linear', NaN);
end
Q = rowQuantile(M, [0.25 0.5 0.75]);

entry.nNotes = n;
entry.peakMedian = peakQ(2); entry.peakQ1 = peakQ(1); entry.peakQ3 = peakQ(3);
entry.ttpMedian  = ttpQ(2);  entry.ttpQ1  = ttpQ(1);  entry.ttpQ3  = ttpQ(3);
entry.plateauTrendMedian = ptQ(2); entry.plateauTrendQ1 = ptQ(1); entry.plateauTrendQ3 = ptQ(3);
entry.jitterMedian = jitQ(2); entry.jitterQ1 = jitQ(1); entry.jitterQ3 = jitQ(3);
entry.grid = tGrid; entry.curveMedian = Q(:,2); entry.curveQ1 = Q(:,1); entry.curveQ3 = Q(:,3);
end


% ========================================================================
function q = quantileNT(x)
% [Q1, median, Q3], type-7 quantile, NaN-omitting, no toolbox.
x = x(~isnan(x));
n = numel(x);
if n == 0, q = [NaN NaN NaN]; return; end
x = sort(x);
if n == 1, q = [x(1) x(1) x(1)]; return; end
ps = [0.25 0.5 0.75];
pos = ps*(n-1) + 1;
lo = floor(pos); hi = ceil(pos); frac = pos - lo;
lo = min(max(lo,1),n); hi = min(max(hi,1),n);
q = x(lo).*(1-frac) + x(hi).*frac;
end


% ========================================================================
function Q = rowQuantile(M, ps)
nR = size(M,1); k = numel(ps);
Q = nan(nR, k);
for i = 1:nR
    v = M(i, :);
    v = v(isfinite(v));
    n = numel(v);
    if n == 0, continue; end
    v = sort(v);
    if n == 1, Q(i, :) = v; continue; end
    pos  = ps*(n-1) + 1;
    lo   = floor(pos); hi = ceil(pos);
    frac = pos - lo;
    Q(i, :) = v(lo).*(1-frac) + v(hi).*frac;
end
end


% ========================================================================
function v = tcol(T, names)
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
function out = emptyOut()
s = struct('profileId',{},'lane',{},'category',{},'nNotes',{}, ...
    'peakMedian',{},'peakQ1',{},'peakQ3',{}, 'ttpMedian',{},'ttpQ1',{},'ttpQ3',{}, ...
    'plateauTrendMedian',{},'plateauTrendQ1',{},'plateauTrendQ3',{}, ...
    'jitterMedian',{},'jitterQ1',{},'jitterQ3',{}, ...
    'grid',{},'curveMedian',{},'curveQ1',{},'curveQ3',{});
out.perPlayer = s; out.population = s; out.attribution = table();
end


% ========================================================================
function drawProfileGrid(entries, categories, titleStr)
% 4x2 grid: rows = lane 0-3, columns = Tap, Hold. `entries` is the flat
% struct array of matching (lane,category) rows for ONE player or the
% population.
fig = figure('Color','w', 'Name',titleStr, 'Position',[80 40 820 980]);
tl = tiledlayout(fig, 4, 2, 'TileSpacing','compact', 'Padding','compact');
title(tl, titleStr, 'FontWeight','bold', 'Interpreter','none');

colOf = containers.Map({'Tap','Hold'}, {[0 0.45 0.74],[0.85 0.33 0.10]});
FINGER_NAMES = {'Index','Middle','Ring','Pinky'};   % canonical, post-remap (see load_recording.m)

for l = 0:3
    finger = FINGER_NAMES{l+1};
    for c = 1:2
        ax = nexttile(tl);
        cat = categories{c};
        sel = entries([entries.lane]==l & [entries.category]==string(cat));
        if isempty(sel)
            axis(ax,'off');
            title(ax, sprintf('%s — %s (no data)', finger, cat), 'FontSize',9);
            continue;
        end
        e = sel(1);
        col = colOf(cat);
        hold(ax,'on'); grid(ax,'on'); box(ax,'on');
        patch(ax, [e.grid; flipud(e.grid)], [e.curveQ1; flipud(e.curveQ3)], col, ...
              'FaceAlpha',0.20, 'EdgeColor','none');
        plot(ax, e.grid, e.curveMedian, '-', 'Color',col, 'LineWidth',1.8);
        xline(ax, 0, ':', 'Color',[0.4 0.4 0.4]);

        if cat == "Hold"
            txt = sprintf(['%s — %s (n=%d)\npeak=%.3g  ttp=%.2fs\n' ...
                'plateau trend=%.3g /s  jitter=%.3g'], ...
                finger, cat, e.nNotes, e.peakMedian, e.ttpMedian, e.plateauTrendMedian, e.jitterMedian);
        else
            txt = sprintf('%s — %s (n=%d)\npeak=%.3g  ttp=%.2fs', ...
                finger, cat, e.nNotes, e.peakMedian, e.ttpMedian);
        end
        title(ax, txt, 'FontSize',8);
        xlabel(ax, 't - onset (s)'); ylabel(ax, 'force');
    end
end
end
