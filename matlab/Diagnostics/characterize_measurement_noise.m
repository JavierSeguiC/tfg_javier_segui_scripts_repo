function out = characterize_measurement_noise(root, varargin)
%CHARACTERIZE_MEASUREMENT_NOISE  How noisy is player performance, per area?
%
%   out = CHARACTERIZE_MEASUREMENT_NOISE(root) analyses every PURE-PI
%   session under `root` and characterizes noise on THREE axes that are
%   deliberately kept separate (mixing them would blur what's actually
%   being measured):
%
%   1. FILTERED steady-state noise, per loop (5 loops). Reuses
%      compute_loop_metrics's own sigmaSS -- the std of the loop's
%      ESTIMATOR output (e_dot / M_F,l) over [t_stab, end]. This is what
%      the controller itself "sees": noise after the estimator's own EMA/
%      point-process smoothing. Reported both in absolute units and as a
%      dimensionless ratio sigmaSS/|r|, since the five loops are on
%      different scales and only the ratio is directly comparable.
%
%   2. RAW per-event steady-state variability, for timing and force
%      separately. Unlike (1), this is computed on the RAW per-note
%      samples from noteOutcomes (timingError; forceMargin per lane), not
%      the filtered control stream -- i.e. the noise BEFORE any estimator
%      smoothing. Restricted to notes occurring after that loop's own
%      t_stab, so this is "how variable is the player's raw response once
%      the system has actually converged", not conflated with transient
%      behaviour. This subsumes timing_error_distribution.m (now scoped to
%      steady-state only, whereas player_ability_summary.m's timing
%      histogram deliberately stays whole-session -- different questions).
%
%   3. Distribution shape (Gaussianity check), AT THE POPULATION LEVEL.
%      Each session's steady-state samples are Z-SCORED with that session's
%      OWN mean/std before pooling across all sessions. This matters:
%      pooling RAW values from sessions with different means/variances
%      would test a Gaussian MIXTURE, not the shape of the noise itself --
%      even if every session's noise were individually perfectly Gaussian,
%      the naive raw pool generally would not look Gaussian. After
%      z-scoring, the pooled sample should look like a standard normal if
%      the per-session Gaussian-noise assumption holds; histogram + normal
%      fit + skewness + +-1/+-2 sigma coverage (vs 68.3%/95.4%) are
%      reported per quantity. This isn't decorative -- every bias/trend
%      test in compute_loop_metrics implicitly leans on approximate
%      normality, so this is the diagnostic that tells you whether that
%      assumption is reasonable to state in the thesis.
%
%   Deliberately NOT computed: decorrelation time (rho1/n_eff are used
%   internally by compute_loop_metrics but not re-reported or explored
%   here), and heteroscedasticity (sigma vs level).
%
%   Sessions are filtered to isPI & ~isRuleBased, same rule as
%   aggregate_loop_metrics.m / compare_controllers_difficulty_trace.m.
%
%   out = CHARACTERIZE_MEASUREMENT_NOISE(root, Name, Value, ...) accepts:
%     Plot          (true)  draw the 3 summary figures.
%     DwellSec, BandK, Alpha, MaxIter, InitTailFrac, RefReflex, RefForce
%                          forwarded to compute_loop_metrics / build_loop_signals.
%     Quiet         (true)  suppress per-folder missing-file warnings.
%
%   RETURNS out, a struct with:
%     .loopNoise    table, one row per (session, loop) THAT STABILIZED:
%                   folder, profileId, loopIdx, loopName, muSS, sigmaSS, r,
%                   normRatio (= sigmaSS/|r|).
%     .timingNoise  table, one row per session with >=5 valid steady-state
%                   timing samples: folder, profileId, n, mean, std, skewness.
%     .forceNoise   table, one row per (session, lane) with >=5 valid
%                   steady-state force-margin samples: folder, profileId,
%                   lane, n, mean, std, skewness.
%     .summary      table, long format: quantity, profileId, n, Q1, median,
%                   Q3. One row per (quantity, player) -- median/IQR of
%                   THAT player's own sessions -- plus one POPULATION row
%                   (pooled across all sessions) as the LAST row of each
%                   quantity's block. Quantities: 5 loops x
%                   {sigmaSS, sigmaSS/|r|}, timing raw std, 4x force raw std.
%
%   Requires: load_recording.m, build_session_index.m, build_loop_signals.m,
%   compute_loop_metrics.m on the path. Base MATLAB only otherwise (no
%   Statistics Toolbox). R2024b baseline.

p = inputParser;
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
opt.Plot  = logical(opt.Plot);
opt.Quiet = logical(opt.Quiet);

% ---- Session selection: pure PI only -----------------------------------
idx = build_session_index(root, 'HeavyStats', false, 'Quiet', opt.Quiet);
if height(idx) == 0
    warning('characterize_measurement_noise:noSessions', 'No sessions found under %s', root);
    out = emptyOut(); return;
end
piMask = idx.isPI & ~idx.isRuleBased;
nMixed = nnz(idx.isPI & idx.isRuleBased);
if nMixed > 0
    fprintf('Note: %d mixed-authority session(s) excluded.\n', nMixed);
end
rows = idx(piMask, :);
if height(rows) == 0
    warning('characterize_measurement_noise:noPI', 'No pure-PI sessions found under %s', root);
    out = emptyOut(); return;
end
fprintf('Characterizing noise over %d pure-PI session(s)...\n', height(rows));

buildOpt = struct('ReflexOnly', false, 'RefReflex', opt.RefReflex, 'RefForce', opt.RefForce);
loopNames = {'Reflex','Force Index','Force Middle','Force Ring','Force Pinky'};

loopRows   = cell(0, 8);
timingRows = cell(0, 6);
forceRows  = cell(0, 7);

pooledTimingZ = [];                   % pooled Z-SCORED steady-state timingError, all sessions
pooledForceZ  = cell(1,4);            % pooled Z-SCORED steady-state forceMargin, per lane

for s = 1:height(rows)
    folder = char(rows.folder(s));
    % Use the player's display name (sessionMeta 'name') for reporting, not
    % the profileId GUID — profileId is only a fallback if name is missing.
    pid = char(rows.name_(s));
    if isempty(pid) || strcmp(pid,"<missing>"), pid = char(rows.profileId(s)); end
    if isempty(pid) || strcmp(pid,"<missing>"), pid = char(rows.name(s)); end

    try
        rec = load_recording(folder, 'Streams', {'meta','notes','control'}, 'Quiet', opt.Quiet);
        if isempty(rec.control)
            warning('characterize_measurement_noise:noControl', 'No control data in %s — skipped.', folder);
            continue;
        end
        [loops, t] = build_loop_signals(rec, buildOpt);
    catch ME
        warning('characterize_measurement_noise:skip', 'Skipping %s: %s', folder, ME.message);
        continue;
    end

    tStabByLoop = nan(1, numel(loops));   % tStabByLoop(L): loop L's own stabilization time

    % ---- (1) Filtered steady-state noise, per loop -----------------------
    for L = 1:numel(loops)
        m = compute_loop_metrics(t, loops(L).y, loops(L).u, loops(L).r, ...
            'DwellSec',opt.DwellSec, 'BandK',opt.BandK, 'Alpha',opt.Alpha, ...
            'MaxIter',opt.MaxIter, 'InitTailFrac',opt.InitTailFrac);
        if ~m.stabilized, continue; end   % noise only meaningful once converged
        tStabByLoop(L) = m.tStab;
        r = loops(L).r;
        normRatio = m.sigmaSS / max(abs(r), eps);
        loopRows(end+1,:) = { string(folder), string(pid), L, string(loopNames{L}), ...
            m.muSS, m.sigmaSS, r, normRatio }; %#ok<AGROW>
    end

    % ---- (2) Raw per-event steady-state variability ------------------------
    if isempty(rec.notes), continue; end
    N = rec.notes;
    if ~all(ismember({'t_session','correctLane','lane'}, N.Properties.VariableNames))
        continue;
    end
    tN  = double(N.t_session);
    cl  = logical(N.correctLane);
    lane = double(N.lane);

    % Timing: reflex loop is loops(1).
    if isfinite(tStabByLoop(1)) && ismember('timingError', N.Properties.VariableNames)
        te = double(N.timingError);
        mask = isfinite(te) & cl & (tN >= tStabByLoop(1));
        x = te(mask);
        if numel(x) >= 5
            xMean = mean(x); xStd = std(x);
            timingRows(end+1,:) = { string(folder), string(pid), numel(x), xMean, xStd, skewnessNT(x) }; %#ok<AGROW>
            % Pool the Z-SCORE (this session's own mean/std removed), not the
            % raw value: pooling raw values across sessions with different
            % means/scales would test a mixture of Gaussians, not the shape
            % of the noise itself -- even if every session were individually
            % perfectly Gaussian, the naive pool generally would not be.
            if xStd > 0
                pooledTimingZ = [pooledTimingZ; (x(:) - xMean) / xStd]; %#ok<AGROW>
            end
        end
    end

    % Force: loops(L) for L=2..5 correspond to lane 0..3.
    if ismember('forceMargin', N.Properties.VariableNames)
        fm = double(N.forceMargin);
        for L = 2:numel(loops)
            ln = L - 2;   % lane index 0..3
            if ~isfinite(tStabByLoop(L)), continue; end
            mask = isfinite(fm) & cl & (lane == ln) & (tN >= tStabByLoop(L));
            x = fm(mask);
            if numel(x) >= 5
                xMean = mean(x); xStd = std(x);
                forceRows(end+1,:) = { string(folder), string(pid), ln, numel(x), xMean, xStd, skewnessNT(x) }; %#ok<AGROW>
                if xStd > 0
                    pooledForceZ{ln+1} = [pooledForceZ{ln+1}; (x(:) - xMean) / xStd];
                end
            end
        end
    end
end

loopT = cell2table(loopRows, 'VariableNames', ...
    {'folder','profileId','loopIdx','loopName','muSS','sigmaSS','r','normRatio'});
timingT = cell2table(timingRows, 'VariableNames', ...
    {'folder','profileId','n','mean','std','skewness'});
forceT = cell2table(forceRows, 'VariableNames', ...
    {'folder','profileId','lane','n','mean','std','skewness'});

% ---- Per-player, per-quantity summary (median/IQR), population row last -
summaryT = buildPlayerSummary(loopT, timingT, forceT, loopNames);

fprintf('\n=== characterize_measurement_noise summary (per player, per quantity; POPULATION row last in each block) ===\n');
disp(summaryT);

if opt.Plot
    drawFilteredNoise(loopT, loopNames);
    drawRawVariability(timingT, forceT);
    drawDistributionShape(pooledTimingZ, pooledForceZ);
end

out.loopNoise   = loopT;
out.timingNoise = timingT;
out.forceNoise  = forceT;
out.summary     = summaryT;
end % ===================== end main =====================================


% ========================================================================
function out = emptyOut()
out.loopNoise   = table();
out.timingNoise = table();
out.forceNoise  = table();
out.summary     = table();
end


% ========================================================================
function g1 = skewnessNT(x)
% Sample skewness (biased/population form, matching the classic textbook
% definition and Statistics Toolbox's default), base MATLAB only.
x = x(:); n = numel(x);
if n < 3, g1 = NaN; return; end
mu = mean(x); sd = sqrt(mean((x-mu).^2));   % biased std (divisor n)
if sd <= 0, g1 = NaN; return; end
g1 = mean((x-mu).^3) / sd^3;
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
function T = buildPlayerSummary(loopT, timingT, forceT, loopNames)
% One table: for each quantity (5 loops x {sigmaSS, normRatio}, timing std,
% 4x force std), one row per player (median/IQR of THAT player's sessions)
% followed by a POPULATION row (median/IQR pooled across ALL sessions,
% profileId = "POPULATION") as the last row of that quantity's block.
blocks = {};

for L = 1:5
    vals = loopT.sigmaSS(loopT.loopIdx == L);
    pid  = loopT.profileId(loopT.loopIdx == L);
    blocks{end+1} = quantileBlock(sprintf('%s: sigmaSS', loopNames{L}), pid, vals); %#ok<AGROW>

    vals = loopT.normRatio(loopT.loopIdx == L);
    pid  = loopT.profileId(loopT.loopIdx == L);
    blocks{end+1} = quantileBlock(sprintf('%s: sigmaSS/|r|', loopNames{L}), pid, vals); %#ok<AGROW>
end

blocks{end+1} = quantileBlock('Timing: raw std (s)', timingT.profileId, timingT.std);

FINGER_NAMES = {'Index','Middle','Ring','Pinky'};   % canonical, post-remap (see load_recording.m)
for ln = 0:3
    sel = forceT.lane == ln;
    blocks{end+1} = quantileBlock(sprintf('Force %s: raw std', FINGER_NAMES{ln+1}), ...
        forceT.profileId(sel), forceT.std(sel)); %#ok<AGROW>
end

T = vertcat(blocks{:});
end


% ========================================================================
function T = quantileBlock(label, profileIds, values)
% Per-player rows (median/IQR of that player's own values) followed by one
% POPULATION row (median/IQR pooled across all values), for one quantity.
profileIds = string(profileIds);
players = unique(profileIds, 'stable');
rows = cell(0,6);
for k = 1:numel(players)
    v = values(profileIds == players(k));
    q = quantileNT(v);
    rows(end+1,:) = { string(label), players(k), numel(v), q(1), q(2), q(3) }; %#ok<AGROW>
end
q = quantileNT(values);
rows(end+1,:) = { string(label), "POPULATION", numel(values(~isnan(values))), q(1), q(2), q(3) };
T = cell2table(rows, 'VariableNames', {'quantity','profileId','n','Q1','median','Q3'});
end


% ========================================================================
function drawFilteredNoise(loopT, loopNames)
% Figure 1: sigmaSS and normalized ratio, one box per loop (5 boxes each).
nLoops = 5;
fig = figure('Color','w', 'Name','Filtered steady-state noise (per loop)', ...
             'Position',[80 80 900 620]);
tl = tiledlayout(fig, 2, 1, 'TileSpacing','compact', 'Padding','compact');
title(tl, 'Filtered (estimator-output) steady-state noise, per loop', 'FontWeight','bold');

ax1 = nexttile(tl); hold(ax1,'on'); grid(ax1,'on');
for L = 1:nLoops
    drawBox(ax1, L, loopT.sigmaSS(loopT.loopIdx == L), [0 0.45 0.74]);
end
ax1.XTick = 1:nLoops; ax1.XTickLabel = loopNames;
xlim(ax1,[0.4 nLoops+0.6]); ylabel(ax1,'\sigma_{ss}  (absolute units)');
title(ax1, 'Absolute steady-state std of the estimator output');

ax2 = nexttile(tl); hold(ax2,'on'); grid(ax2,'on');
for L = 1:nLoops
    drawBox(ax2, L, loopT.normRatio(loopT.loopIdx == L), [0.85 0.33 0.10]);
end
ax2.XTick = 1:nLoops; ax2.XTickLabel = loopNames;
xlim(ax2,[0.4 nLoops+0.6]); ylabel(ax2,'\sigma_{ss} / |r|');
title(ax2, 'Normalized noise ratio (dimensionless, comparable across loops)');
end


% ========================================================================
function drawRawVariability(timingT, forceT)
% Figure 2: raw per-event steady-state std -- timing (1 box) and force per
% lane (4 boxes), kept in separate panels since the units differ.
fig = figure('Color','w', 'Name','Raw per-event steady-state variability', ...
             'Position',[80 80 800 620]);
tl = tiledlayout(fig, 2, 1, 'TileSpacing','compact', 'Padding','compact');
title(tl, 'Raw (pre-filter) steady-state variability of the player''s response', 'FontWeight','bold');

ax1 = nexttile(tl); hold(ax1,'on'); grid(ax1,'on');
drawBox(ax1, 1, timingT.std, [0.47 0.67 0.19]);
ax1.XTick = 1; ax1.XTickLabel = {'Timing'};
xlim(ax1,[0.4 1.6]); ylabel(ax1,'std of raw timingError  [s]');
title(ax1, 'Timing-error std, steady-state notes only (one point per session)');

ax2 = nexttile(tl); hold(ax2,'on'); grid(ax2,'on');
FINGER_NAMES = {'Index','Middle','Ring','Pinky'};   % canonical, post-remap (see load_recording.m)
for ln = 0:3
    drawBox(ax2, ln+1, forceT.std(forceT.lane == ln), [0.49 0.18 0.56]);
end
ax2.XTick = 1:4; ax2.XTickLabel = FINGER_NAMES;
xlim(ax2,[0.4 4.6]); ylabel(ax2,'std of raw forceMargin');
title(ax2, 'Force-margin std, steady-state notes only, per finger (one point per session)');
end


% ========================================================================
function drawDistributionShape(pooledTimingZ, pooledForceZ)
% Figure 3: Gaussianity check at the POPULATION level. Each session is
% Z-SCORED with its OWN mean/std before pooling (x - mean_session)/std_session,
% so a between-session difference in level or scale can't masquerade as
% non-Gaussian shape -- pooling raw values from sessions with different
% means/variances would test a Gaussian MIXTURE, not the noise shape, even
% if every session were individually perfectly Gaussian. Under a valid
% Gaussian-noise assumption the pooled z-scores should look like a
% standard normal, N(0,1); the fitted mu/sigma reported in each panel are
% a direct check of that (mu should land near 0, sigma near 1).
fig = figure('Color','w', 'Name','Distribution shape (population, z-scored)', ...
             'Position',[60 60 1400 500]);
tl = tiledlayout(fig, 1, 5, 'TileSpacing','compact', 'Padding','compact');
title(tl, 'Population-pooled steady-state residuals (per-session z-score) vs Gaussian fit', 'FontWeight','bold');

ax = nexttile(tl);
histAndFit(ax, pooledTimingZ, 'Timing error (z-score)');

for ln = 0:3
    ax = nexttile(tl);
    histAndFit(ax, pooledForceZ{ln+1}, sprintf('Force margin L%d (z-score)', ln));
end
end


% ========================================================================
function histAndFit(ax, x, labelStr)
hold(ax,'on'); grid(ax,'on'); box(ax,'on');
x = x(~isnan(x));
if numel(x) < 5
    axis(ax,'off'); title(ax, sprintf('%s\n(insufficient data)', labelStr));
    return;
end
mu = mean(x); sg = std(x);
histogram(ax, x, 'Normalization','pdf', 'FaceAlpha',0.35, 'FaceColor',[0 0.45 0.74], 'EdgeColor','none');
xx = linspace(min(x), max(x), 200);
plot(ax, xx, normpdfNT(xx, mu, sg), 'r-', 'LineWidth',1.8);
xline(ax, mu, 'r:');
g1 = skewnessNT(x);
w1 = 100*mean(abs(x-mu) <= sg);
w2 = 100*mean(abs(x-mu) <= 2*sg);
xlabel(ax, labelStr);
ylabel(ax, 'pdf');
title(ax, sprintf('%s\n\\mu=%.3g \\sigma=%.3g skew=%.2f\n\\pm1\\sigma:%.0f%% (68.3) \\pm2\\sigma:%.0f%% (95.4)', ...
    labelStr, mu, sg, g1, w1, w2), 'FontSize',8);
end


% ========================================================================
function y = normpdfNT(x, mu, sigma)
% Normal pdf, base MATLAB only (no Statistics Toolbox normpdf).
if sigma <= 0, y = zeros(size(x)); return; end
y = exp(-0.5*((x-mu)/sigma).^2) / (sigma*sqrt(2*pi));
end


% ========================================================================
function drawBox(ax, xpos, vals, col)
% Minimal box-and-whisker at x = xpos: box Q1-Q3, median line, whiskers to
% min/max, jittered raw points underneath. Same convention as
% aggregate_loop_metrics.m's drawBox (kept local/duplicated -- MATLAB local
% functions are file-scoped, so this is not a naming conflict).
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
hw = 0.28;

plot(ax, [xpos xpos], [vmin q1], '-', 'Color',col, 'LineWidth',1.2);
plot(ax, [xpos xpos], [q3 vmax], '-', 'Color',col, 'LineWidth',1.2);
patch(ax, xpos+[-hw hw hw -hw], [q1 q1 q3 q3], col, 'FaceAlpha',0.15, ...
      'EdgeColor',col, 'LineWidth',1.2);
plot(ax, xpos+[-hw hw], [med med], '-', 'Color',col, 'LineWidth',2.2);
end
