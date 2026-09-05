function m = compute_loop_metrics(t, y, u, r, varargin)
%COMPUTE_LOOP_METRICS  Statistical convergence/bias/trend analysis of one
%   control loop's measured output y(t), regulated toward setpoint r, plus
%   a drift-trend analysis of its control action u(t).
%
%   This is the SHARED statistics engine behind evaluate_control_loops.m,
%   player_ability_summary.m, and any other script that needs "when did
%   this loop stabilize / is it biased / is it drifting" computed the same
%   way. Do not reimplement this logic elsewhere -- extend it here so every
%   caller stays consistent.
%
%   No smoothing anywhere: t_stab is found by a self-consistent band
%   search on the raw samples, and bias/trend significance are corrected
%   for autocorrelation rather than assuming independent samples.
%
%   m = COMPUTE_LOOP_METRICS(t, y, u, r) with default options, or
%   m = COMPUTE_LOOP_METRICS(t, y, u, r, Name, Value, ...) with:
%     DwellSec     (20)   seconds y must remain in the steady band for an
%                         entry to count as stabilization (rides out
%                         momentary lapses -- see findStabilization).
%     BandK        (2)    band half-width in steady-state std devs
%                         (mu +/- K*sigma).
%     Alpha        (0.05) significance level for the bias and trend tests.
%     MaxIter      (10)   max iterations for the self-consistent t_stab
%                         search.
%     InitTailFrac (0.34) tail fraction used to SEED the first band before
%                         the iteration refines it, and used as the
%                         fallback trend-fit region if no stable region is
%                         found at all.
%
%   METHOD
%   * Stabilization point (t_stab). Self-consistent iterative estimate. A
%     steady band mu_ss +/- BandK*sigma_ss is computed from a candidate
%     tail; t_stab is the FIRST time y enters that band AND then stays
%     inside it for at least DwellSec seconds. mu_ss/sigma_ss are then
%     recomputed on [t_stab, end], and the process repeats until t_stab
%     stops moving. This single point anchors every other metric.
%       - rise time   = t_stab - t0   (time to first sustained band entry)
%       - settle time = t_stab - t0   (same event, by definition)
%   * Converged & bias. One-sample test of the steady-state mean against
%     the reference r, H0: mu_ss = r, corrected for autocorrelation via an
%     effective sample size n_eff = n*(1-rho1)/(1+rho1). Reject -> a real,
%     non-zero bias (reported with its CI); fail to reject -> converged to
%     r within noise.
%   * Steady-state control action. Mean/median of u over [t_stab,end],
%     restricted to samples where y itself is inside the steady band (so a
%     momentary y excursion doesn't drag the reported u average with it).
%     This is the "capacity" figure other scripts want (e.g. steady-state
%     difficulty, or tau + margin for a force loop) -- computed once, here,
%     so nobody re-derives their own ad hoc band for it.
%   * Trend (control action only). OLS slope of u(t) over the POST-stab
%     region only (the transient would dominate a whole-session slope),
%     with the same n_eff correction on its significance. If no stable
%     region was found, the slope is taken over the whole tail and flagged
%     uTrendWholeTail = true.
%   * Noise. steady-state sigma and the lag-1 autocorrelation / effective
%     sample size the corrections above rest on.
%
%   Overshoot, setpoint crossings, decay ratio and damping are intentionally
%   NOT computed: under this noise they measure noise, not dynamics.
%
%   RETURNS a struct m with fields (NaN / false where not defined):
%     t0, tEnd
%     tStab, stabilized
%     riseTime, settleTime          (= tStab - t0)
%     muSS, sigmaSS                 steady-state mean & std of y
%     bandLo, bandHi                the steady band actually used
%     bias, biasCI, biasP, biasSignificant
%     nEff, rho1
%     uMeanSS, uMedianSS            steady-state control-action level
%     uTrend, uTrendCI, uTrendP, uTrendSignificant, uTrendWholeTail
%
%   Requires base MATLAB only (no toolboxes). R2024b baseline.

p = inputParser;
p.addParameter('DwellSec',    20,    @(x)isnumeric(x)&&isscalar(x)&&x>0);
p.addParameter('BandK',       2,     @(x)isnumeric(x)&&isscalar(x)&&x>0);
p.addParameter('Alpha',       0.05,  @(x)isnumeric(x)&&isscalar(x)&&x>0&&x<1);
p.addParameter('MaxIter',     10,    @(x)isnumeric(x)&&isscalar(x)&&x>=1);
p.addParameter('InitTailFrac',0.34,  @(x)isnumeric(x)&&isscalar(x)&&x>0&&x<1);
p.parse(varargin{:});
opt = p.Results;

m = initMetrics();

fin = isfinite(t) & isfinite(y);
tt = t(fin); yy = y(fin);
if numel(yy) < 10, return; end
[tt, ord] = sort(tt(:)); yy = yy(ord);
m.t0 = tt(1); m.tEnd = tt(end);

uu = [];
if ~isempty(u)
    uf = u(fin); uu = uf(ord);
end

% ---- Self-consistent stabilization point ------------------------------
[tStab, muSS, sigmaSS, ssMask, ok] = findStabilization(tt, yy, opt);
m.stabilized = ok;
m.muSS = muSS; m.sigmaSS = sigmaSS;
if ok
    m.tStab = tStab;
    m.riseTime = tStab - m.t0;
    m.settleTime = tStab - m.t0;
    m.bandLo = muSS - opt.BandK*sigmaSS;
    m.bandHi = muSS + opt.BandK*sigmaSS;
end

% ---- Bias test: is the steady-state mean actually at r? ---------------
if nnz(ssMask) >= 5
    ssRes = yy(ssMask) - r;
    [rho1, nEff] = effectiveN(yy(ssMask));
    m.rho1 = rho1; m.nEff = nEff;
    m.bias = mean(ssRes);
    seBias = std(yy(ssMask),0) / sqrt(max(nEff,1));
    tcrit = tCritical(1-opt.Alpha/2, max(nEff-1,1));
    m.biasCI = tcrit * seBias;
    if seBias > 0
        tstat = m.bias / seBias;
        m.biasP = 2*(1 - tCDF(abs(tstat), max(nEff-1,1)));
    end
    m.biasSignificant = isfinite(m.biasP) && m.biasP < opt.Alpha;
    m.sigmaSS = std(yy(ssMask),0);   % refine on the final SS region
end

% ---- Steady-state control-action level (for "capacity"-style summaries) -
if ok && ~isempty(uu)
    ssBandMask = (tt >= tStab) & (yy >= m.bandLo) & (yy <= m.bandHi) & isfinite(uu);
    if nnz(ssBandMask) >= 1
        m.uMeanSS   = mean(uu(ssBandMask));
        m.uMedianSS = median(uu(ssBandMask));
    end
end

% ---- Trend on the control action over the post-stab region ------------
if ~isempty(uu)
    if ok
        trMask = tt >= tStab;
        m.uTrendWholeTail = false;
    else
        % No stable region: fall back to whole tail, flag loudly.
        trMask = tt >= (m.tEnd - opt.InitTailFrac*(m.tEnd - m.t0));
        m.uTrendWholeTail = true;
    end
    if nnz(trMask) >= 5
        [slopePerSec, slopeCIsec, slopeP] = olsSlopeAC(tt(trMask), uu(trMask), opt.Alpha);
        m.uTrend   = slopePerSec * 60;      % report per-minute
        m.uTrendCI = slopeCIsec * 60;
        m.uTrendP  = slopeP;
        m.uTrendSignificant = isfinite(slopeP) && slopeP < opt.Alpha;
    end
end
end % ===================== end main =====================================


% ========================================================================
function m = initMetrics()
m = struct('t0',NaN,'tEnd',NaN,'tStab',NaN,'stabilized',false, ...
    'riseTime',NaN,'settleTime',NaN,'muSS',NaN,'sigmaSS',NaN, ...
    'bandLo',NaN,'bandHi',NaN,'bias',NaN,'biasCI',NaN,'biasP',NaN, ...
    'biasSignificant',false,'nEff',NaN,'rho1',NaN, ...
    'uMeanSS',NaN,'uMedianSS',NaN, ...
    'uTrend',NaN,'uTrendCI',NaN,'uTrendP',NaN,'uTrendSignificant',false, ...
    'uTrendWholeTail',false);
end


% ========================================================================
function [tStab, muSS, sigmaSS, ssMask, ok] = findStabilization(t, y, opt)
% Self-consistent stabilization search.
%   1. Seed the steady band from the final InitTailFrac of the record.
%   2. t_stab = first time y enters [mu-K*sig, mu+K*sig] and then stays in
%      it for >= DwellSec continuous seconds (brief exits allowed only if
%      they end before the dwell has been satisfied -- see firstSustained).
%   3. Recompute mu,sig on [t_stab, end]; repeat until t_stab is stable.
% Returns ok=false if no qualifying sustained entry ever exists.
tEnd = t(end); t0 = t(1);
seedMask = t >= (tEnd - opt.InitTailFrac*(tEnd - t0));
if nnz(seedMask) < 3
    tStab = NaN; muSS = NaN; sigmaSS = NaN; ssMask = false(size(t)); ok = false;
    return;
end
mu = mean(y(seedMask),'omitnan');
sig = std(y(seedMask),0,'omitnan');
if ~(sig > 0), sig = max(eps, std(y,0,'omitnan')); end

tStab = NaN; ok = false;
for it = 1:opt.MaxIter
    lo = mu - opt.BandK*sig; hi = mu + opt.BandK*sig;
    tS = firstSustained(t, y, lo, hi, opt.DwellSec);
    if isnan(tS)
        ok = false; break;
    end
    newMask = t >= tS;
    newMu  = mean(y(newMask),'omitnan');
    newSig = std(y(newMask),0,'omitnan');
    converged = isfinite(tStab) && abs(tS - tStab) < 1e-6;
    tStab = tS; mu = newMu; sig = max(newSig, eps); ok = true;
    if converged, break; end
end

if ok
    ssMask = t >= tStab; muSS = mean(y(ssMask),'omitnan'); sigmaSS = std(y(ssMask),0,'omitnan');
else
    tStab = NaN; muSS = mean(y(seedMask),'omitnan'); sigmaSS = std(y(seedMask),0,'omitnan');
    ssMask = seedMask;   % so bias/noise still have a (flagged) region to use
end
end


% ========================================================================
function tS = firstSustained(t, y, lo, hi, dwellSec)
% First time index where y enters [lo,hi] and then remains inside it
% continuously for at least dwellSec seconds (to the end of the record if
% the record ends first but the dwell is already met). Momentary earlier
% dips that break before dwellSec are skipped -- we look for the FIRST entry
% that leads to a sustained residence.
inBand = (y >= lo) & (y <= hi);
n = numel(t);
i = 1;
tS = NaN;
while i <= n
    if ~inBand(i), i = i + 1; continue; end
    % start of an in-band run at i; find where it breaks
    j = i;
    while j+1 <= n && inBand(j+1), j = j + 1; end
    runDur = t(j) - t(i);
    if runDur >= dwellSec
        tS = t(i); return;     % this entry is sustained
    end
    i = j + 1;                 % run too short: skip past it, keep scanning
end
end


% ========================================================================
function [rho1, nEff] = effectiveN(x)
% Lag-1 autocorrelation and effective sample size for an autocorrelated
% series: nEff = n*(1-rho1)/(1+rho1), clamped to [1, n]. A 10 Hz slice of a
% ~10-30 s EMA has far fewer independent samples than raw n; every
% significance test in this file uses nEff instead of n for exactly that
% reason.
x = x(:); n = numel(x);
if n < 3, rho1 = 0; nEff = n; return; end
x = x - mean(x);
denom = sum(x.^2);
if denom <= 0, rho1 = 0; nEff = n; return; end
rho1 = sum(x(1:end-1).*x(2:end)) / denom;
rho1 = max(min(rho1, 0.999), -0.999);
nEff = n * (1 - rho1) / (1 + rho1);
nEff = max(1, min(nEff, n));
end


% ========================================================================
function [slopePerSec, slopeCI, pVal] = olsSlopeAC(t, u, alpha)
% OLS slope of u on t, with the slope's standard error inflated for lag-1
% autocorrelation of the residuals (same nEff idea, applied to regression:
% the effective dof for the slope test is reduced, so a slow drift isn't
% declared significant just because there are thousands of correlated points).
t = t(:); u = u(:);
n = numel(t);
tc = t - mean(t);
Stt = sum(tc.^2);
if Stt <= 0, slopePerSec = NaN; slopeCI = NaN; pVal = NaN; return; end
slopePerSec = sum(tc .* (u - mean(u))) / Stt;
intercept = mean(u) - slopePerSec*mean(t);
resid = u - (intercept + slopePerSec*t);
% autocorrelation-corrected residual variance / dof
[rho1, nEff] = effectiveN(resid); %#ok<ASGLU>
sigma2 = sum(resid.^2) / max(n-2,1);
% inflate slope variance by n/nEff (fewer independent residuals)
seSlope = sqrt(sigma2 / Stt) * sqrt(n / max(nEff,1));
dof = max(nEff - 2, 1);
tcrit = tCritical(1 - alpha/2, dof);
slopeCI = tcrit * seSlope;
if seSlope > 0
    pVal = 2*(1 - tCDF(abs(slopePerSec/seSlope), dof));
else
    pVal = NaN;
end
end


% ========================================================================
function p = tCDF(x, v)
% Student-t CDF at x with v dof, via the regularized incomplete beta, using
% only base MATLAB (betainc). Falls back to the normal CDF for large v.
if v <= 0, p = NaN; return; end
if v > 2e5
    p = 0.5*(1 + erf(x/sqrt(2)));   % normal approximation
    return;
end
xb = v / (v + x.^2);
ib = 0.5 * betainc(xb, v/2, 0.5);
if x >= 0, p = 1 - ib; else, p = ib; end
end


% ========================================================================
function x = tCritical(pp, v)
% Inverse Student-t (quantile) at probability pp, v dof, base MATLAB only
% (bisection on tCDF). pp in (0,1). Used for CI half-widths.
if v <= 0, x = NaN; return; end
lo = -1e4; hi = 1e4;
for k = 1:200
    mid = 0.5*(lo+hi);
    if tCDF(mid, v) < pp, lo = mid; else, hi = mid; end
end
x = 0.5*(lo+hi);
end
