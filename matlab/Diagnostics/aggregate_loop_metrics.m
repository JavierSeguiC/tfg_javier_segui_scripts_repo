function out = aggregate_loop_metrics(root, varargin)
%AGGREGATE_LOOP_METRICS  Population-level control-loop performance across sessions.
%
%   out = AGGREGATE_LOOP_METRICS(root) runs the SAME shared engine as
%   evaluate_control_loops.m / player_ability_summary.m
%   (load_recording -> build_loop_signals -> compute_loop_metrics) across
%   every PURE-PI session found under `root`, and reports the DISTRIBUTION
%   of stabilization / bias / trend across sessions, per loop. A single
%   session's figure is an example; this is the result that actually
%   belongs in the thesis ("the reflex loop stabilized in 9/10 sessions,
%   median t_stab = 74s, IQR [58,96]").
%
%   Sessions are filtered to isPI & ~isRuleBased (same rule as
%   compare_controllers_difficulty_trace.m): a mixed-authority session's
%   loop behaviour isn't attributable to PI alone.
%
%   out = AGGREGATE_LOOP_METRICS(root, Name, Value, ...) accepts:
%     ReflexOnly    (false) aggregate only loop 1 (skip the 4 force loops --
%                          use this if most sessions are keyboard-only).
%     Plot          (true)  draw the summary boxplot figure.
%     DwellSec, BandK, Alpha, MaxIter, InitTailFrac, RefReflex, RefForce
%                          forwarded to compute_loop_metrics / build_loop_signals
%                          -- see their own headers for meaning/defaults.
%     Quiet         (true)  suppress per-folder missing-file warnings.
%
%   RETURNS out, a struct with:
%     .long     table, one row per (session, loop): folder, profileId,
%               loopIdx, loopName, stabilized, tStab, muSS, sigmaSS, bias,
%               biasSignificant, nEff, rho1, uTrend, uTrendSignificant,
%               uTrendWholeTail. The raw material -- slice this yourself
%               for anything not already in .summary (e.g. by player).
%     .summary  table, one row per loop (5, or 1 if ReflexOnly): nSessions,
%               nStabilized, stabRate, tStab_Q1/median/Q3, bias_Q1/median/Q3,
%               biasSig_frac, sigmaSS_Q1/median/Q3, trendSig_frac,
%               trend_median_whenSig, trend_nPositiveSig, trend_nNegativeSig.
%               Median/Q1/Q3 use the type-7 quantile (matches numpy/R
%               default), computed only over sessions where the underlying
%               quantity is defined (e.g. tStab only over stabilized
%               sessions).
%
%   Requires: load_recording.m, build_session_index.m, build_loop_signals.m,
%   compute_loop_metrics.m on the path. Base MATLAB only otherwise (no
%   Statistics Toolbox -- boxplots are custom-drawn). R2024b baseline.

p = inputParser;
p.addParameter('ReflexOnly',   false, @(x)islogical(x)||isnumeric(x));
p.addParameter('Plot',         true,  @(x)islogical(x)||isnumeric(x));
p.addParameter('DwellSec',     20,    @(x)isnumeric(x)&&isscalar(x)&&x>0);
p.addParameter('BandK',        2,     @(x)isnumeric(x)&&isscalar(x)&&x>0);
p.addParameter('Alpha',        0.05,  @(x)isnumeric(x)&&isscalar(x)&&x>0&&x<1);
p.addParameter('MaxIter',      10,    @(x)isnumeric(x)&&isscalar(x)&&x>=1);
p.addParameter('InitTailFrac', 0.34,  @(x)isnumeric(x)&&isscalar(x)&&x>0&&x<1);
p.addParameter('RefReflex',    10,    @(x)isnumeric(x)&&isscalar(x));
p.addParameter('RefForce',     0.05,  @(x)isnumeric(x)&&(isscalar(x)||numel(x)==4));
p.addParameter('Quiet',        true,  @(x)islogical(x)||isnumeric(x));
p.parse(varargin{:});
opt = p.Results;
opt.ReflexOnly = logical(opt.ReflexOnly);
opt.Plot       = logical(opt.Plot);
opt.Quiet      = logical(opt.Quiet);

% ---- Session selection: pure PI only -----------------------------------
idx = build_session_index(root, 'HeavyStats', false, 'Quiet', opt.Quiet);
if height(idx) == 0
    warning('aggregate_loop_metrics:noSessions', 'No sessions found under %s', root);
    out = struct('long', emptyLongTable(), 'summary', table());
    return;
end
piMask = idx.isPI & ~idx.isRuleBased;
nMixed = nnz(idx.isPI & idx.isRuleBased);
if nMixed > 0
    fprintf(['Note: %d mixed-authority session(s) excluded -- aggregation ' ...
             'needs sessions attributable to PI alone.\n'], nMixed);
end
rows = idx(piMask, :);
if height(rows) == 0
    warning('aggregate_loop_metrics:noPI', 'No pure-PI sessions found under %s', root);
    out = struct('long', emptyLongTable(), 'summary', table());
    return;
end
fprintf('Aggregating %d pure-PI session(s)...\n', height(rows));

% ---- Per session, per loop: run the shared engine -----------------------
buildOpt = struct('ReflexOnly', opt.ReflexOnly, 'RefReflex', opt.RefReflex, ...
                   'RefForce', opt.RefForce);
loopNamesFull = {'Reflex','Force Index','Force Middle','Force Ring','Force Pinky'};

longRows = cell(0, 19);
for s = 1:height(rows)
    folder = char(rows.folder(s));
    pid = char(rows.profileId(s));
    if isempty(pid) || strcmp(pid,"<missing>"), pid = char(rows.name(s)); end

    try
        rec = load_recording(folder, 'Streams', {'meta','notes','control'}, 'Quiet', opt.Quiet);
        if isempty(rec.control)
            warning('aggregate_loop_metrics:noControl', 'No control data in %s — skipped.', folder);
            continue;
        end
        [loops, t] = build_loop_signals(rec, buildOpt);
    catch ME
        warning('aggregate_loop_metrics:skip', 'Skipping %s: %s', folder, ME.message);
        continue;
    end

    for L = 1:numel(loops)
        m = compute_loop_metrics(t, loops(L).y, loops(L).u, loops(L).r, ...
            'DwellSec',opt.DwellSec, 'BandK',opt.BandK, 'Alpha',opt.Alpha, ...
            'MaxIter',opt.MaxIter, 'InitTailFrac',opt.InitTailFrac);

        longRows(end+1,:) = { string(folder), string(pid), L, string(loopNamesFull{L}), ...
            m.stabilized, m.tStab, m.muSS, m.sigmaSS, m.bias, m.biasCI, m.biasP, ...
            m.biasSignificant, m.nEff, m.rho1, ...
            m.uTrend, m.uTrendCI, m.uTrendP, m.uTrendSignificant, m.uTrendWholeTail }; %#ok<AGROW>
    end
end

if isempty(longRows)
    warning('aggregate_loop_metrics:noData', 'No loop data extracted from any session.');
    out = struct('long', emptyLongTable(), 'summary', table());
    return;
end

longT = cell2table(longRows, 'VariableNames', ...
    {'folder','profileId','loopIdx','loopName','stabilized','tStab','muSS','sigmaSS', ...
     'bias','biasCI','biasP','biasSignificant','nEff','rho1', ...
     'uTrend','uTrendCI','uTrendP','uTrendSignificant','uTrendWholeTail'});

% ---- Per-loop summary ----------------------------------------------------
loopIdxPresent = unique(longT.loopIdx);
summRows = cell(numel(loopIdxPresent), 1);
for k = 1:numel(loopIdxPresent)
    L = loopIdxPresent(k);
    sub = longT(longT.loopIdx == L, :);
    summRows{k} = summariseLoop(loopNamesFull{L}, sub);
end
summaryT = vertcat(summRows{:});

fprintf('\n=== aggregate_loop_metrics summary ===\n');
disp(summaryT);

if opt.Plot
    drawSummary(summaryT, longT);
end

out.long = longT;
out.summary = summaryT;
end % ===================== end main =====================================


% ========================================================================
function T = summariseLoop(name, sub)
% One summary row for a single loop across all its sessions in `sub`.
n = height(sub);
nStab = nnz(sub.stabilized);
if n > 0, stabRate = nStab/n; else, stabRate = NaN; end

% t_stab: only defined for stabilized sessions.
tStabQ = quantileNT(sub.tStab(sub.stabilized));

% bias: only defined where compute_loop_metrics actually ran the test
% (nnz(ssMask)>=5 inside it -- reflected here simply by ~isnan(bias)).
hasBias = ~isnan(sub.bias);
biasQ = quantileNT(sub.bias(hasBias));
if nnz(hasBias) > 0
    biasSigFrac = nnz(sub.biasSignificant(hasBias)) / nnz(hasBias);
else
    biasSigFrac = NaN;
end

sigmaQ = quantileNT(sub.sigmaSS(hasBias));   % sigmaSS defined alongside bias

% trend: report among sessions where a trend was computed at all, and
% separately the median magnitude/direction restricted to SIGNIFICANT ones.
hasTrend = ~isnan(sub.uTrend);
if nnz(hasTrend) > 0
    trendSigFrac = nnz(sub.uTrendSignificant(hasTrend)) / nnz(hasTrend);
else
    trendSigFrac = NaN;
end
sigTrendVals = sub.uTrend(hasTrend & sub.uTrendSignificant);
trendMedianWhenSig = medianOrNaN(sigTrendVals);
trendNPos = nnz(sigTrendVals > 0);
trendNNeg = nnz(sigTrendVals < 0);

T = table(string(name), n, nStab, stabRate, ...
    tStabQ(1), tStabQ(2), tStabQ(3), ...
    biasQ(1), biasQ(2), biasQ(3), biasSigFrac, ...
    sigmaQ(1), sigmaQ(2), sigmaQ(3), ...
    trendSigFrac, trendMedianWhenSig, trendNPos, trendNNeg, ...
    'VariableNames', {'loopName','nSessions','nStabilized','stabRate', ...
    'tStab_Q1','tStab_median','tStab_Q3', ...
    'bias_Q1','bias_median','bias_Q3','biasSig_frac', ...
    'sigmaSS_Q1','sigmaSS_median','sigmaSS_Q3', ...
    'trendSig_frac','trend_median_whenSig','trend_nPositiveSig','trend_nNegativeSig'});
end


% ========================================================================
function v = medianOrNaN(x)
x = x(~isnan(x));
if isempty(x), v = NaN; else, v = median(x); end
end


% ========================================================================
function q = quantileNT(x)
% [Q1, median, Q3] via the type-7 quantile (matches numpy/R default),
% NaN-omitting, no Statistics Toolbox dependency. Returns [NaN NaN NaN] if
% there are no finite values.
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
function T = emptyLongTable()
T = table('Size',[0 19], 'VariableTypes', ...
    {'string','string','double','string','logical','double','double','double', ...
     'double','double','double','logical','double','double', ...
     'double','double','double','logical','logical'}, ...
    'VariableNames', {'folder','profileId','loopIdx','loopName','stabilized','tStab', ...
     'muSS','sigmaSS','bias','biasCI','biasP','biasSignificant','nEff','rho1', ...
     'uTrend','uTrendCI','uTrendP','uTrendSignificant','uTrendWholeTail'});
end


% ========================================================================
function drawSummary(summaryT, longT)
% One figure, 3 stacked panels (t_stab, bias, trend), custom box-and-
% whisker per loop (no Statistics Toolbox: box = Q1-Q3, line = median,
% whiskers = min/max, jittered raw points overlaid -- honest at the small
% N this data realistically has).
nLoops = height(summaryT);
loopNames = cellstr(summaryT.loopName);

fig = figure('Color','w', 'Name','aggregate_loop_metrics — summary', ...
             'Position',[100 80 max(700, 160*nLoops+200) 780]);
tl = tiledlayout(fig, 3, 1, 'TileSpacing','compact', 'Padding','compact');
title(tl, 'Control-loop performance across sessions (pure PI)', 'FontWeight','bold');

% ---- Panel 1: t_stab (only stabilized sessions) ------------------------
ax1 = nexttile(tl); hold(ax1,'on'); grid(ax1,'on');
xticklabelsStab = cell(nLoops,1);
for L = 1:nLoops
    sub = longT(longT.loopIdx == L, :);
    vals = sub.tStab(sub.stabilized);
    drawBox(ax1, L, vals, [0 0.45 0.74]);
    xticklabelsStab{L} = sprintf('%s\n(%d/%d stab.)', loopNames{L}, ...
        summaryT.nStabilized(L), summaryT.nSessions(L));
end
ax1.XTick = 1:nLoops; ax1.XTickLabel = xticklabelsStab;
xlim(ax1, [0.4 nLoops+0.6]);
ylabel(ax1, 't_{stab}  [s]');
title(ax1, 'Time to stabilization (rise = settle), stabilized sessions only');

% ---- Panel 2: bias (colour-coded by significance) -----------------------
ax2 = nexttile(tl); hold(ax2,'on'); grid(ax2,'on');
yline(ax2, 0, '--', 'Color',[0.4 0.4 0.4], 'HandleVisibility','off');
for L = 1:nLoops
    sub = longT(longT.loopIdx == L, :);
    hasBias = ~isnan(sub.bias);
    drawBox(ax2, L, sub.bias(hasBias), [0.85 0.33 0.10]);
    % overlay significance-coloured points (drawBox already scatters grey;
    % re-scatter on top coloured so a real bias is visually obvious)
    vals = sub.bias(hasBias); sig = sub.biasSignificant(hasBias);
    jit = (rand(nnz(hasBias),1)-0.5)*0.25;
    cSig = [0.80 0.10 0.10]; cNs = [0.55 0.55 0.55];
    if any(sig),  plot(ax2, L+jit(sig),  vals(sig),  'o', 'MarkerSize',4, ...
                  'MarkerFaceColor',cSig, 'MarkerEdgeColor','none'); end
    if any(~sig), plot(ax2, L+jit(~sig), vals(~sig), 'o', 'MarkerSize',4, ...
                  'MarkerFaceColor',cNs, 'MarkerEdgeColor','none'); end
end
ax2.XTick = 1:nLoops; ax2.XTickLabel = loopNames;
xlim(ax2, [0.4 nLoops+0.6]);
ylabel(ax2, 'bias = \mu_{ss} - r');
title(ax2, 'Steady-state bias vs reference (red = statistically significant, gray = n.s.)');

% ---- Panel 3: control-action trend (colour-coded by significance) -------
ax3 = nexttile(tl); hold(ax3,'on'); grid(ax3,'on');
yline(ax3, 0, '--', 'Color',[0.4 0.4 0.4], 'HandleVisibility','off');
for L = 1:nLoops
    sub = longT(longT.loopIdx == L, :);
    hasTrend = ~isnan(sub.uTrend);
    drawBox(ax3, L, sub.uTrend(hasTrend), [0.47 0.67 0.19]);
    vals = sub.uTrend(hasTrend); sig = sub.uTrendSignificant(hasTrend);
    jit = (rand(nnz(hasTrend),1)-0.5)*0.25;
    cSig = [0.49 0.18 0.56]; cNs = [0.55 0.55 0.55];
    if any(sig),  plot(ax3, L+jit(sig),  vals(sig),  'o', 'MarkerSize',4, ...
                  'MarkerFaceColor',cSig, 'MarkerEdgeColor','none'); end
    if any(~sig), plot(ax3, L+jit(~sig), vals(~sig), 'o', 'MarkerSize',4, ...
                  'MarkerFaceColor',cNs, 'MarkerEdgeColor','none'); end
end
ax3.XTick = 1:nLoops; ax3.XTickLabel = loopNames;
xlim(ax3, [0.4 nLoops+0.6]);
ylabel(ax3, 'control-action drift  [units/min]');
xlabel(ax3, 'loop');
title(ax3, 'Post-stabilization drift of the control action (purple = significant, gray = n.s.)');
end


% ========================================================================
function drawBox(ax, xpos, vals, col)
% Minimal box-and-whisker at x = xpos: box Q1-Q3, median line, whiskers to
% min/max, jittered raw points underneath (drawn first, so the
% significance-coloured re-scatter in drawSummary sits on top).
vals = vals(~isnan(vals));
n = numel(vals);
if n == 0, return; end

jit = (rand(n,1)-0.5) * 0.25;
plot(ax, xpos+jit, vals, 'o', 'MarkerSize',4, ...
     'MarkerFaceColor',[0.75 0.75 0.75], 'MarkerEdgeColor','none');

if n == 1
    plot(ax, xpos, vals, '_', 'MarkerSize',18, 'Color',col, 'LineWidth',2);
    return;
end

q = quantileNT(vals);
q1 = q(1); med = q(2); q3 = q(3);
vmin = min(vals); vmax = max(vals);
hw = 0.28;   % half box width

% whiskers
plot(ax, [xpos xpos], [vmin q1], '-', 'Color',col, 'LineWidth',1.2);
plot(ax, [xpos xpos], [q3 vmax], '-', 'Color',col, 'LineWidth',1.2);
% box
patch(ax, xpos+[-hw hw hw -hw], [q1 q1 q3 q3], col, 'FaceAlpha',0.15, ...
      'EdgeColor',col, 'LineWidth',1.2);
% median
plot(ax, xpos+[-hw hw], [med med], '-', 'Color',col, 'LineWidth',2.2);
end
