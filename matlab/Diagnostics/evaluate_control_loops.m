function results = evaluate_control_loops(folders, varargin)
%EVALUATE_CONTROL_LOOPS  Assess closed-loop DDA controller performance from recordings.
%
%   Produces, per recording folder, one figure showing reference / control
%   action / measured output for each of the five PI loops (1 reflex + 4
%   force), overlaid with per-loop success/mistake event markers, the
%   detected stabilization point and its steady-state band, and a metrics
%   panel rendered INSIDE the figure window.
%
%   This script is a thin orchestrator: LOAD_RECORDING loads the CSVs,
%   BUILD_LOOP_SIGNALS assembles the 5 loops' y/u/r + event markers, and
%   COMPUTE_LOOP_METRICS does the statistics. All three are shared with
%   player_ability_summary.m / aggregate_loop_metrics.m -- see those files'
%   headers for the same METHOD section reproduced there. Do not
%   reimplement any of this here; extend the shared files instead.
%
% USAGE
%   evaluate_control_loops()                       % pick a folder via dialog
%   evaluate_control_loops(folder)                 % one recording, or a parent
%                                                  %   containing recording_* subfolders
%   evaluate_control_loops({f1,f2,...})            % explicit list of recordings
%   R = evaluate_control_loops(..., Name,Value)    % options + return struct
%
%   Each figure has a "Full range / Fit signal" TOGGLE BUTTON (top-left) that
%   rescales the control-action row between its full possible span (d/v: 0..max
%   recorded; tau: 0..1) and auto-fit. This is a view control, so it's a button,
%   not an argument.
%
% NAME-VALUE OPTIONS
%   'ReflexOnly'  (logical, false) analyse only loop 1 (keyboard sessions have
%                                  no force sensor -> force loops are disabled).
%   'RefReflex'   (10)   reflex setpoint r for e_dot, in errors/min. If the
%                        session's sessionMeta has targetErrorsPerMinute, that
%                        is used instead (per-session, authoritative).
%   'RefForce'    (0.05) force-margin setpoint r (scalar, or 1x4 per lane).
%                        Overridden per-session by sessionMeta.targetForceMargin
%                        when present.
%   'DwellSec'    (20)   seconds the signal must remain in the band for an entry
%                        to count as stabilization (rides out momentary lapses).
%   'BandK'       (2)    band half-width in steady-state std devs (mu +/- K*sigma).
%   'Alpha'       (0.05) significance level for the bias and trend tests.
%   'MaxIter'     (10)   max iterations for the self-consistent t_stab search.
%   'InitTailFrac'(0.34) tail fraction used to SEED the first band before the
%                        iteration refines it.
%   'TimeAlign'   ('auto') 'auto' | 'raw' | 'sharedZero'. How to align note-event
%                        timestamps to the control-stream clock.
%   'Plot'        (true) draw the per-recording figure. Set false for a pure
%                        computation pass (e.g. from a batch/aggregation
%                        script) with zero figures created.
%
% RETURNS
%   results : struct array (one element per processed recording) with fields
%             .folder, .controllers, .multiController, .loops (per-loop
%             signals + .metrics, i.e. build_loop_signals's output with
%             compute_loop_metrics's output attached to each loop).
%
% Requires: load_recording.m, build_loop_signals.m, compute_loop_metrics.m
% on the path. Base MATLAB only otherwise. R2024b baseline.
%
% Author: control-loop diagnostics tool for the DDA rehab-game TFG.

% ------------------------------------------------------------------------
% 0. Parse inputs
% ------------------------------------------------------------------------
if nargin < 1, folders = []; end

p = inputParser;
p.addParameter('ReflexOnly',  false, @(x)islogical(x)||isnumeric(x));
p.addParameter('RefReflex',   10,    @(x)isnumeric(x)&&isscalar(x));
p.addParameter('RefForce',    0.05,  @(x)isnumeric(x)&&(isscalar(x)||numel(x)==4));
p.addParameter('DwellSec',    20,    @(x)isnumeric(x)&&isscalar(x)&&x>0);
p.addParameter('BandK',       2,     @(x)isnumeric(x)&&isscalar(x)&&x>0);
p.addParameter('Alpha',       0.05,  @(x)isnumeric(x)&&isscalar(x)&&x>0&&x<1);
p.addParameter('MaxIter',     10,    @(x)isnumeric(x)&&isscalar(x)&&x>=1);
p.addParameter('InitTailFrac',0.34,  @(x)isnumeric(x)&&isscalar(x)&&x>0&&x<1);
p.addParameter('TimeAlign',   'auto', @(s)any(strcmpi(s,{'auto','raw','sharedZero'})));
p.addParameter('Plot',        true,  @(x)islogical(x)||isnumeric(x));
p.parse(varargin{:});
opt = p.Results;
opt.ReflexOnly = logical(opt.ReflexOnly);
opt.Plot       = logical(opt.Plot);
if isscalar(opt.RefForce), opt.RefForce = repmat(opt.RefForce,1,4); end

% ------------------------------------------------------------------------
% 1. Resolve the list of recording folders
% ------------------------------------------------------------------------
recFolders = resolveFolders(folders);
if isempty(recFolders)
    warning('No recording folders selected. Nothing to do.');
    results = struct([]); return;
end

results = struct('folder',{},'controllers',{},'multiController',{},'loops',{});
for i = 1:numel(recFolders)
    try
        results(end+1) = processRecording(recFolders{i}, opt); %#ok<AGROW>
    catch ME
        warning('Skipping "%s": %s', recFolders{i}, ME.message);
    end
end
end % ===================== end main =====================================


% ========================================================================
function recFolders = resolveFolders(folders)
% Turn the user's argument into a cell array of recording-folder paths.
if isempty(folders)
    d = uigetdir(pwd, 'Select a recording folder (or a parent of recording_* folders)');
    if isequal(d,0), recFolders = {}; return; end
    folders = {d};
elseif ischar(folders) || isstring(folders)
    folders = cellstr(folders);
end

recFolders = {};
for k = 1:numel(folders)
    f = char(folders{k});
    % If this folder directly contains a controlAction_*.csv, treat it as a recording.
    if ~isempty(dir(fullfile(f,'controlAction_*.csv')))
        recFolders{end+1} = f; %#ok<AGROW>
    else
        % Otherwise expand any recording_* subfolders inside it.
        sub = dir(fullfile(f,'recording_*'));
        sub = sub([sub.isdir]);
        if isempty(sub)
            % Last resort: any immediate subdir that looks like a recording.
            sub = dir(f); sub = sub([sub.isdir] & ~ismember({sub.name},{'.','..'}));
        end
        for s = 1:numel(sub)
            cand = fullfile(f, sub(s).name);
            if ~isempty(dir(fullfile(cand,'controlAction_*.csv')))
                recFolders{end+1} = cand; %#ok<AGROW>
            end
        end
    end
end
recFolders = unique(recFolders,'stable');
end


% ========================================================================
function out = processRecording(folder, opt)
[~, recName] = fileparts(strip_trailing_sep(folder));

lrec = load_recording(folder, 'Streams', {'meta','notes','control'}, 'Quiet', true);
if isempty(lrec.control)
    error('No controlAction_*.csv data in %s', folder);
end

[loops, t] = build_loop_signals(lrec, opt);

for L = 1:numel(loops)
    loops(L).metrics = compute_loop_metrics(t, loops(L).y, loops(L).u, loops(L).r, ...
        'DwellSec',opt.DwellSec, 'BandK',opt.BandK, 'Alpha',opt.Alpha, ...
        'MaxIter',opt.MaxIter, 'InitTailFrac',opt.InitTailFrac);
end

% ---- Controller banner (from load_recording's own parse of controllersUsed)
ctrlList = string(lrec.controllers);
multiController = numel(ctrlList) > 1;
if multiController
    warning('Recording "%s" used more than one controller: %s', ...
            recName, strjoin(cellstr(ctrlList), ', '));
    ctrlBanner = sprintf('WARNING: multiple controllers (%s)', strjoin(cellstr(ctrlList),'; '));
elseif isscalar(ctrlList)
    ctrlBanner = sprintf('%s controller', ctrlList(1));
else
    ctrlBanner = 'controller type unknown';
end

if opt.Plot
    drawRecording(recName, ctrlBanner, multiController, t, loops, opt);
end

out.folder          = folder;
out.controllers     = cellstr(ctrlList);
out.multiController = multiController;
out.loops            = loops;
end


% ========================================================================
function drawRecording(recName, ctrlBanner, multiController, t, loops, opt)
nL = numel(loops);
fig = figure('Name', sprintf('Control-loop evaluation — %s', recName), ...
             'Color','w', 'Units','normalized', ...
             'Position', [0.04 0.06 min(0.16*nL+0.06,0.95) 0.86]);

tl = tiledlayout(fig, 3, nL, 'TileSpacing','compact', 'Padding','compact');
titleColor = [0 0 0];
if multiController, titleColor = [0.75 0 0]; end
title(tl, sprintf('%s   (%s)', recName, ctrlBanner), ...
      'Interpreter','none', 'FontWeight','bold', 'Color', titleColor);

ax1s = gobjects(nL,1);   % control-action axes, for the toggle callback
uKinds = cell(nL,1);
uData  = cell(nL,1);

for L = 1:nL
    lp = loops(L); mtr = lp.metrics;

    % ---- Row 1: control action + drift trend --------------------------
    ax1 = nexttile(tl, L);
    hold(ax1,'on'); grid(ax1,'on');
    plot(ax1, t, lp.u, '-', 'Color',[0.20 0.20 0.20], 'LineWidth',1.1);
    % drift trend line over the region it was fitted on
    if isfinite(mtr.uTrend)
        if mtr.stabilized && ~mtr.uTrendWholeTail
            tA = mtr.tStab;
        else
            tA = mtr.tEnd - opt.InitTailFrac*(mtr.tEnd - mtr.t0);
        end
        seg = t(t >= tA & isfinite(t));
        if ~isempty(seg)
            % reconstruct the fitted line: slope (per sec) through the region mean
            inMask = t >= tA & isfinite(lp.u(:));
            um = mean(lp.u(inMask),'omitnan'); tm = mean(t(inMask),'omitnan');
            slopeSec = mtr.uTrend/60;
            trendLine = um + slopeSec*(seg - tm);
            trCol = tern(mtr.uTrendWholeTail, [0.85 0.4 0], [0.60 0.0 0.60]);
            plot(ax1, seg, trendLine, '-', 'Color', trCol, 'LineWidth', 1.6);
        end
    end
    ax1.XTickLabel = [];
    ylabel(ax1, lp.uLabel);
    title(ax1, lp.name, 'Interpreter','tex', 'FontSize',10);
    hold(ax1,'off');
    ax1s(L) = ax1; uKinds{L} = lp.uKind; uData{L} = lp.u;

    % ---- Row 2: reference + measured + stab point + steady band -------
    ax2 = nexttile(tl, nL + L);
    hold(ax2,'on'); grid(ax2,'on');
    hMeas = plot(ax2, t, lp.y, '-', 'Color',[0 0.45 0.74], 'LineWidth',1.0);
    hRef  = yline(ax2, lp.r, '--', 'Color',[0.85 0.33 0.10], 'LineWidth',1.3);

    hBand = []; hStab = [];
    if mtr.stabilized
        % steady-state band mu +/- K*sigma, from t_stab to end
        xb = [mtr.tStab mtr.tEnd mtr.tEnd mtr.tStab];
        yb = [mtr.bandLo mtr.bandLo mtr.bandHi mtr.bandHi];
        hBand = patch(ax2, xb, yb, [0.30 0.65 0.30], 'FaceAlpha',0.15, 'EdgeColor','none');
        plot(ax2, [mtr.tStab mtr.tEnd], [mtr.muSS mtr.muSS], '-', ...
             'Color',[0.10 0.50 0.10], 'LineWidth',1.0);
        hStab = xline(ax2, mtr.tStab, '-', 'Color',[0.10 0.50 0.10], 'LineWidth',1.5);
    end

    % event rug near the bottom of the measured axis
    yl = ylim(ax2);
    if isempty(yl) || ~all(isfinite(yl)), yl = [0 1]; end
    rugY = yl(1) + 0.04*range(yl);
    hG = []; hR = [];
    if ~isempty(lp.evT)
        g = lp.evGreen;
        if any(g)
            hG = plot(ax2, lp.evT(g), rugY*ones(1,nnz(g)), '.', ...
                'Color',[0.10 0.65 0.10], 'MarkerSize',9);
        end
        if any(~g)
            hR = plot(ax2, lp.evT(~g), rugY*ones(1,nnz(~g)), '.', ...
                'Color',[0.85 0.10 0.10], 'MarkerSize',9);
        end
        ylim(ax2, [yl(1)-0.02*range(yl), yl(2)]);
    end
    xlabel(ax2,'session time [s]'); ylabel(ax2, lp.yLabel, 'Interpreter','tex');
    if L == 1
        h = [hMeas, hRef]; lab = {'measured y','reference r'};
        if ~isempty(hStab), h(end+1)=hStab; lab{end+1}='t_{stab}'; end
        if ~isempty(hBand), h(end+1)=hBand; lab{end+1}='\mu\pm2\sigma band'; end
        if ~isempty(hG), h(end+1)=hG; lab{end+1}='success'; end
        if ~isempty(hR), h(end+1)=hR; lab{end+1}='mistake'; end
        legend(ax2, h, lab, 'Location','best', 'FontSize',7, 'Box','off');
    end
    hold(ax2,'off');

    % ---- Row 3: metrics text panel ------------------------------------
    ax3 = nexttile(tl, 2*nL + L);
    axis(ax3,'off');
    text(ax3, 0, 1, metricsText(mtr, lp), 'Units','normalized', ...
        'VerticalAlignment','top', 'FontName','Courier New', 'FontSize',8.5, ...
        'Interpreter','none');
end

% ---- View toggle button: full-range vs fit for the control-action row -
state.full = false;
state.ax1s = ax1s; state.uKinds = uKinds; state.uData = uData;
btn = uicontrol(fig, 'Style','togglebutton', 'String','Full range: OFF', ...
    'Units','normalized', 'Position',[0.005 0.965 0.11 0.03], ...
    'FontSize',8, 'BackgroundColor',[0.94 0.94 0.94]);
btn.Callback = @(src,~) toggleFullRange(src, state);
end


% ========================================================================
function toggleFullRange(src, state)
% Rescale every control-action axis between full possible range and auto-fit.
full = logical(src.Value);
if full, src.String = 'Full range: ON'; else, src.String = 'Full range: OFF'; end
for L = 1:numel(state.ax1s)
    ax = state.ax1s(L);
    if ~isgraphics(ax), continue; end
    if ~full
        ylim(ax, 'auto');
    else
        switch state.uKinds{L}
            case 'tau'
                ylim(ax, [0 1]);
            case {'d','v'}
                uMax = max(state.uData{L}, [], 'omitnan');
                if isfinite(uMax) && uMax > 0, ylim(ax, [0 uMax]); end
        end
    end
end
end


% ========================================================================
function s = metricsText(m, lp)
fmt = @(v,u) tern(isfinite(v), sprintf('%.3g %s',v,u), 'n/a');
yn  = @(b) tern(b, 'yes', 'no');

% Stabilization block
if m.stabilized
    stabStr  = sprintf('%.3g s', m.tStab);
    riseStr  = sprintf('%.3g s', m.riseTime);
else
    stabStr  = 'NOT STABILIZED';
    riseStr  = 'n/a';
end

% Bias verdict (only meaningful once we have a steady region)
if isfinite(m.bias)
    if m.biasSignificant
        biasVerdict = sprintf('bias = %.3g +/- %.2g %s (p=%.3g) REAL', ...
            m.bias, m.biasCI, lp.yUnit, m.biasP);
    else
        biasVerdict = sprintf('bias = %.3g %s (p=%.3g) ~ at r', ...
            m.bias, lp.yUnit, m.biasP);
    end
else
    biasVerdict = 'n/a';
end

% Trend block
if isfinite(m.uTrend)
    sig = tern(m.uTrendSignificant, 'SIGNIFICANT', 'n.s.');
    trendStr = sprintf('%.3g +/- %.2g /min (p=%.3g) %s', ...
        m.uTrend, m.uTrendCI, m.uTrendP, sig);
    if m.uTrendWholeTail
        trendNote = '  region  : WHOLE TAIL (no stable region -- interpret w/ care)';
    else
        trendNote = '  region  : post-stabilization';
    end
else
    trendStr = 'n/a'; trendNote = '';
end

s = sprintf([ ...
    'STABILIZATION\n' ...
    '  stabilized  : %s\n' ...
    '  t_stab      : %s\n' ...
    '  rise=settle : %s\n' ...
    'STEADY STATE (raw samples on [t_stab,end])\n' ...
    '  mu_ss       : %s\n' ...
    '  sigma_ss    : %s\n' ...
    '  n_eff/rho1  : %s / %s\n' ...
    '  vs ref r    : %s\n' ...
    'CONTROL-ACTION TREND (%s)\n' ...
    '  drift       : %s\n' ...
    '%s'], ...
    yn(m.stabilized), stabStr, riseStr, ...
    fmt(m.muSS, lp.yUnit), fmt(m.sigmaSS, lp.yUnit), ...
    fmt(m.nEff,''), fmt(m.rho1,''), biasVerdict, ...
    lp.uLabel, trendStr, trendNote);
end


% ========================================================================
% ---- small utilities ---------------------------------------------------
function out = tern(c,a,b), if c, out=a; else, out=b; end, end

function s = strip_trailing_sep(s)
if ~isempty(s) && (s(end)=='/'||s(end)=='\'), s(end)=[]; end
end
