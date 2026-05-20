#region Usings
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Strategies;
using System.Windows.Media;
using NinjaTrader.Gui.Tools;
using System.Linq;
#endregion

/*
 * ╔══════════════════════════════════════════════════════════════════════════╗
 * ║                        Scalp20_34  –  v3.4                             ║
 * ║         Gerado: 20/05/2026  |  Autor: Idom                             ║
 * ╠══════════════════════════════════════════════════════════════════════════╣
 * ║  Operacional: Consolidação EMA20/EMA34 → Breakout com agressão         ║
 * ║               confirmada → Entrada a mercado + D1/D2/D3 + Trail        ║
 * ╠══════════════════════════════════════════════════════════════════════════╣
 * ║  v3.3 – Bug 1 fix: Exit ANTES do SetStopLoss em D1/D2/D3 Long e Short  ║
 * ║         Stop emitido antes da saída parcial mantinha qtd original da    ║
 * ║         posição → ao atingir BE fechava residual + abria Short/Long     ║
 * ║         invertido. Observado em tempo real 19/05/2026: 1ct Short aberto ║
 * ║         após D1 de 3ct em posição Long de 5ct.                          ║
 * ║         Correção: ExitLong/Short disparado primeiro, SetStopLoss        ║
 * ║         reemitido imediatamente após para cobrir apenas o residual.      ║
 * ║  v3.2 – FSM neutra a flags de direção                                  ║
 * ║  v3.1 – Fix1: TODOS os Print() protegidos com guard ModoDebug          ║
 * ║         Fix2: Painel unificado "05 — Gestão" igual à stg20com34        ║
 * ║         Fix3: Padrões corrigidos para baseline v4.1 validado           ║
 * ║         Proteções C1–C4 mantidas da v3.0                               ║
 * ╠══════════════════════════════════════════════════════════════════════════╣
 * ║  v3.0 – Modelo completo D1/D2/D3 + C1-C4 + Short ON/OFF funcional     ║
 * ║  v2.4 – GOLD: Long puro estrutural + Stop Range como padrão            ║
 * ║  v2.3 – D2 dinâmica restrita ao Long                                   ║
 * ║  v2.2 – Base v1.5 gold restaurada | D2 dinâmica MM200 + score         ║
 * ║  v1.5 – Gestão de risco diário | Remoção do filtro horário             ║
 * ║  Instrumentos: MNQ / MES  |  TF: 1m ou 2m                             ║
 * ╚══════════════════════════════════════════════════════════════════════════╝
 */

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum ModoAgressaoEnum { VolumetricBars, DeltaProxy }

    public class Scalp20_34 : Strategy
    {
        // ── Indicadores internos ──────────────────────────────────────────────
        private EMA _ema20, _ema34;
        private SMA _smaVol;
        private MAX _maxHigh;
        private MIN _minLow;
        private ATR _atr;
        private SMA _mm200;

        // ── FSM ───────────────────────────────────────────────────────────────
        private enum Fase { Idle, EmConsolidacao, AguardandoBreakout, Executada }
        private Fase _fase = Fase.Idle;

        // ── Estado da consolidação ────────────────────────────────────────────
        private double _rangeHigh       = 0;
        private double _rangeLow        = 0;
        private double _swingHigh       = 0;
        private double _swingLow        = 0;
        private int    _barConsolInicio = -1;
        private double _deltaAcum       = 0;

        // ── Gestão de saída — modelo completo D1/D2/D3 ───────────────────────
        private bool   _d1Executada  = false;
        private bool   _d2Executada  = false;
        private bool   _d3Executada  = false;
        private bool   _emTrail      = false;
        private bool   _breakEven    = false;
        private int    _qtdInicial   = 0;
        private double _entradaPreco = 0;
        private double _stopInicial  = 0;   // C1: salvo na entrada para clamp D1
        private bool   _fillCompleto = false; // C2: guard de fill em lotes
        private double _precoD2      = 0;
        private double _precoD3      = 0;

        // ── Buffer de delta para score de cessação ────────────────────────────
        private double[] _deltaBuffer;
        private int      _deltaBufferIdx   = 0;
        private bool     _deltaBufferCheio = false;

        // ── Gestão de risco diário ────────────────────────────────────────────
        private int      _entradasHoje    = 0;
        private int      _stopsHoje       = 0;
        private double   _perdaDiariaAcum = 0;
        private DateTime _diaAtual        = DateTime.MinValue;
        private bool     _bloqueioLogado  = false;

        // ─────────────────────────────────────────────────────────────────────
        #region OnStateChange
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Scalp20_34 v3.4 | Bug2 fix: SetStopLoss com quantity residual em D1/D2/D3 | Baseline v4.1";
                Name        = "Scalp20_34";
                Calculate   = Calculate.OnBarClose;

                // 01 - Geral
                CompressaoLimiteTicks = 8;
                ConsolidacaoLookback  = 10;
                VolumeLookback        = 20;
                ModoAgressao          = ModoAgressaoEnum.DeltaProxy;
                DeltaMinimoLong       = 50;
                DeltaMinimoShort      = -50;
                ModoDebug             = false;  // OFF por padrão — sem ruído no log

                // 02 - Direcao
                HabilitarLong  = true;
                HabilitarShort = false;

                // 03 - Gestao de Risco diario
                UsarGestaoRisco   = true;
                MaxTradesPorDia   = 20;
                MaxStopsPorDia    = 5;
                MaxPerdaPorDia    = 500;

                // 05 - Gestao (painel unificado estilo stg20com34)
                Contratos         = 5;
                StopInicialTicks  = 40;   // Stop fixo — usado quando StopRange e StopSwing OFF
                UsarStopRange     = true;  // baseline v4.1: Range ON
                UsarStopSwing     = false;
                StopBufferTicks   = 2;
                StopMaxTicks      = 120;   // 0 = sem limite

                D1AlvoTicks        = 40;   // baseline v4.1
                D1PctContratos     = 60;   // 60%

                UsarD2             = false;
                D2AlvoTicks        = 70;
                D2StopAposSaidaTicks = 20;

                UsarD3             = false;
                D3AlvoTicks        = 100;
                D3StopAposSaidaTicks = 12;

                TrailAposD3Ticks   = 25;   // baseline v4.1
                TrailStepTicks     = 6;

                // 06 - Score Exaustao (D2 dinamica — mantida da v2.4)
                UsarD2Dinamica      = false;
                D2MM200Periodo      = 200;
                D2BufferATRFator    = 0.25;
                D2ATRPeriodo        = 14;
                D2DeltaMediaBars    = 5;
                D2DeltaQuedaPct     = 25.0;
                D2RangeEncolheFator = 0.60;
                D2ScoreMinimo       = 3;

                // NT8
                IsExitOnSessionCloseStrategy              = true;
                ExitOnSessionCloseSeconds                 = 30;
                BarsRequiredToTrade                       = 50;
                IsInstantiatedOnEachOptimizationIteration = true;
            }
            else if (State == State.DataLoaded)
            {
                _ema20   = EMA(20);
                _ema34   = EMA(34);
                _smaVol  = SMA(Volume, VolumeLookback);
                _maxHigh = MAX(High, ConsolidacaoLookback);
                _minLow  = MIN(Low,  ConsolidacaoLookback);
                _atr     = ATR(D2ATRPeriodo);
                _mm200   = SMA(Close, D2MM200Periodo);

                _deltaBuffer = new double[Math.Max(1, D2DeltaMediaBars)];

                // Este print sempre sai — é o único sem guard (inicialização)
                Print("[Scalp20_34] ══ Iniciado em " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss") +
                      " | v3.4 | Long=" + HabilitarLong +
                      " Short=" + HabilitarShort +
                      " StopRange=" + UsarStopRange +
                      " StopSwing=" + UsarStopSwing +
                      " D2=" + UsarD2 +
                      " D3=" + UsarD3 +
                      " StopMax=" + StopMaxTicks + "t ══");
            }
        }
        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region OnBarUpdate
        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade)
                return;

            AtualizarContadoresDiarios();
            AtualizarDeltaBuffer();

            GerenciarSaida();

            if (LimitesDiariosAtingidos())
                return;

            switch (_fase)
            {
                case Fase.Idle:
                    VerificarInicioConsolidacao();
                    break;
                case Fase.EmConsolidacao:
                    AtualizarConsolidacao();
                    break;
                case Fase.AguardandoBreakout:
                    VerificarBreakout();
                    break;
                case Fase.Executada:
                    break;
            }
        }
        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Gestao de risco diaria

        private void AtualizarContadoresDiarios()
        {
            DateTime hoje = Times[0][0].Date;
            if (hoje != _diaAtual)
            {
                _diaAtual        = hoje;
                _entradasHoje    = 0;
                _stopsHoje       = 0;
                _perdaDiariaAcum = 0;
                _bloqueioLogado  = false;

                if (ModoDebug)
                    Print("[Scalp20_34] Novo dia — contadores resetados " + hoje.ToShortDateString());
            }
        }

        private bool LimitesDiariosAtingidos()
        {
            if (!UsarGestaoRisco) return false;

            if (_entradasHoje >= MaxTradesPorDia)
            {
                if (!_bloqueioLogado && ModoDebug)
                {
                    Print("[Scalp20_34] BLOQUEIO — max trades/dia (" + _entradasHoje + ")");
                    _bloqueioLogado = true;
                }
                return true;
            }
            if (_stopsHoje >= MaxStopsPorDia)
            {
                if (!_bloqueioLogado && ModoDebug)
                {
                    Print("[Scalp20_34] BLOQUEIO — max stops/dia (" + _stopsHoje + ")");
                    _bloqueioLogado = true;
                }
                return true;
            }
            if (_perdaDiariaAcum <= -MaxPerdaPorDia)
            {
                if (!_bloqueioLogado && ModoDebug)
                {
                    Print("[Scalp20_34] BLOQUEIO — max perda/dia ($" + _perdaDiariaAcum.ToString("F0") + ")");
                    _bloqueioLogado = true;
                }
                return true;
            }
            return false;
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region OnExecutionUpdate

        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition,
            string orderId, DateTime time)
        {
            if (execution.Order == null) return;

            // Atualiza perda diária
            if (SystemPerformance.AllTrades.Count > 0)
            {
                _perdaDiariaAcum = SystemPerformance.AllTrades
                    .Where(t => t.Exit.Time.Date == _diaAtual)
                    .Sum(t => t.ProfitCurrency);
            }

            // Contagem de stops
            if (execution.Order.OrderState == OrderState.Filled &&
               (execution.Order.OrderType == OrderType.StopMarket ||
                execution.Order.OrderType == OrderType.StopLimit) &&
                execution.Order.Name != "ScalpLong" &&
                execution.Order.Name != "ScalpShort")
            {
                _stopsHoje++;
                if (ModoDebug)
                    Print("[Scalp20_34] Stop executado — total hoje: " + _stopsHoje);
            }

            // C4: detectar StopCancelClose → forçar reset
            if (execution.Order.Name == "StopCancelClose" ||
                execution.Order.Name == "Zerar")
            {
                if (ModoDebug)
                    Print("[Scalp20_34] C4 — StopCancelClose detectado → ResetarFase()");
                if (_fase == Fase.Executada)
                    ResetarFase();
            }
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region FSM - Consolidacao

        private void VerificarInicioConsolidacao()
        {
            double compressao = Math.Abs(_ema20[0] - _ema34[0]) / TickSize;
            if (compressao < CompressaoLimiteTicks)
            {
                _fase            = Fase.EmConsolidacao;
                _barConsolInicio = CurrentBar;
                _rangeHigh       = _maxHigh[0];
                _rangeLow        = _minLow[0];
                _swingHigh       = High[0];
                _swingLow        = Low[0];
                _deltaAcum       = 0;

                if (ModoDebug)
                    Print("[Scalp20_34] CONSOLIDACAO iniciada bar=" + CurrentBar +
                          " RH=" + _rangeHigh + " RL=" + _rangeLow +
                          " Compressao=" + compressao.ToString("F1") + "t");
            }
        }

        private void AtualizarConsolidacao()
        {
            double compressao = Math.Abs(_ema20[0] - _ema34[0]) / TickSize;

            if (compressao >= CompressaoLimiteTicks)
            {
                if (ModoDebug)
                    Print("[Scalp20_34] Consolidacao encerrada sem breakout bar=" + CurrentBar);
                if (_fase != Fase.Executada)
                    ResetarFase();
                return;
            }

            _rangeHigh  = Math.Max(_rangeHigh, High[0]);
            _rangeLow   = Math.Min(_rangeLow,  Low[0]);
            _swingHigh  = Math.Max(_swingHigh, High[0]);
            _swingLow   = Math.Min(_swingLow,  Low[0]);
            _deltaAcum += CalcularDelta();
            _fase = Fase.AguardandoBreakout;
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region FSM - Breakout + Entrada

        private void VerificarBreakout()
        {
            double compressao = Math.Abs(_ema20[0] - _ema34[0]) / TickSize;

            if (compressao < CompressaoLimiteTicks)
            {
                _rangeHigh  = Math.Max(_rangeHigh, High[0]);
                _rangeLow   = Math.Min(_rangeLow,  Low[0]);
                _deltaAcum += CalcularDelta();
                return;
            }

            double delta = CalcularDelta();

            // ── Long ──────────────────────────────────────────────────────────
            if (HabilitarLong &&
                Close[0] > _rangeHigh &&
                Volume[0] > _smaVol[0] &&
                delta >= DeltaMinimoLong)
            {
                // C3: cheque duplo
                if (Position.MarketPosition == MarketPosition.Flat &&
                    Position.Quantity == 0)
                {
                    double stop = CalcularStopLong();

                    if (StopMaxTicks > 0)
                    {
                        double stopTicksCalc = (Close[0] - stop) / TickSize;
                        if (stopTicksCalc > StopMaxTicks)
                        {
                            if (ModoDebug)
                                Print("[Scalp20_34] Long BLOQUEADO stop=" +
                                      stopTicksCalc.ToString("F0") + "t > max=" + StopMaxTicks + "t");
                            // Breakout válido mas stop inviável — range consumido, reseta
                            ResetarFase();
                            return;
                        }
                    }

                    EntrarLong(stop);
                    return;
                }
            }
            else if (HabilitarLong && Close[0] > _rangeHigh && Volume[0] > _smaVol[0] && delta < DeltaMinimoLong)
            {
                // Breakout de preço confirmado mas delta insuficiente — range consumido
                if (ModoDebug)
                    Print("[Scalp20_34] Long BLOQUEADO delta=" + delta.ToString("F0") +
                          " < " + DeltaMinimoLong + " | range consumido → reset");
                ResetarFase();
                return;
            }
            else if (!HabilitarLong && Close[0] > _rangeHigh && Volume[0] > _smaVol[0])
            {
                // Breakout de alta com Long OFF — reseta range para neutralidade
                if (ModoDebug)
                    Print("[Scalp20_34] Breakout Long detectado (dir OFF) → reset range");
                ResetarFase();
                return;
            }

            // ── Short ─────────────────────────────────────────────────────────
            if (Close[0] < _rangeLow && Volume[0] > _smaVol[0] && delta <= DeltaMinimoShort)
            {
                if (HabilitarShort &&
                    Position.MarketPosition == MarketPosition.Flat &&
                    Position.Quantity == 0)
                {
                    double stop = CalcularStopShort();

                    if (StopMaxTicks > 0)
                    {
                        double stopTicksCalc = (stop - Close[0]) / TickSize;
                        if (stopTicksCalc > StopMaxTicks)
                        {
                            if (ModoDebug)
                                Print("[Scalp20_34] Short BLOQUEADO stop=" +
                                      stopTicksCalc.ToString("F0") + "t > max=" + StopMaxTicks + "t");
                            // Breakout ocorreu mas stop inviável — reseta para novo range
                            ResetarFase();
                            return;
                        }
                    }

                    EntrarShort(stop);
                    return;
                }
                else
                {
                    // Breakout de baixa válido mas Short OFF (ou posição aberta)
                    // Reseta a FSM para que o Long não use um range já rompido
                    if (ModoDebug && HabilitarShort)
                        Print("[Scalp20_34] Short BLOQUEADO delta=" + delta.ToString("F0") +
                              " | range consumido → reset");
                    if (ModoDebug && !HabilitarShort)
                        Print("[Scalp20_34] Breakout Short detectado (dir OFF) → reset range");
                    ResetarFase();
                    return;
                }
            }

            // Range expirado
            if (compressao >= CompressaoLimiteTicks * 3)
            {
                if (ModoDebug)
                    Print("[Scalp20_34] Range expirado — resetando");
                if (_fase != Fase.Executada)
                    ResetarFase();
            }
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Execucao de ordens

        private void EntrarLong(double stopPrice)
        {
            _qtdInicial   = Contratos;   // C1: fixo
            _entradaPreco = Close[0];
            _stopInicial  = stopPrice;
            _fillCompleto = false;
            _d1Executada  = false;
            _d2Executada  = false;
            _d3Executada  = false;
            _emTrail      = false;
            _breakEven    = false;
            _precoD2      = 0;
            _precoD3      = 0;
            _fase         = Fase.Executada;
            _entradasHoje++;

            EnterLong(Contratos, "ScalpLong");
            SetStopLoss("ScalpLong", CalculationMode.Price, stopPrice, false);

            if (ModoDebug)
            {
                double stopTicks  = (_entradaPreco - stopPrice) / TickSize;
                double rangeWidth = (_rangeHigh - _rangeLow) / TickSize;
                Print("[Scalp20_34] LONG entrada=" + _entradaPreco.ToString("F2") +
                      " stop=" + stopPrice.ToString("F2") +
                      " stopTicks=" + stopTicks.ToString("F0") + "t" +
                      " rangeWidth=" + rangeWidth.ToString("F0") + "t" +
                      " qtd=" + Contratos);
            }
        }

        private void EntrarShort(double stopPrice)
        {
            _qtdInicial   = Contratos;   // C1: fixo
            _entradaPreco = Close[0];
            _stopInicial  = stopPrice;
            _fillCompleto = false;
            _d1Executada  = false;
            _d2Executada  = false;
            _d3Executada  = false;
            _emTrail      = false;
            _breakEven    = false;
            _precoD2      = 0;
            _precoD3      = 0;
            _fase         = Fase.Executada;
            _entradasHoje++;

            EnterShort(Contratos, "ScalpShort");
            SetStopLoss("ScalpShort", CalculationMode.Price, stopPrice, false);

            if (ModoDebug)
            {
                double stopTicks  = (stopPrice - _entradaPreco) / TickSize;
                double rangeWidth = (_rangeHigh - _rangeLow) / TickSize;
                Print("[Scalp20_34] SHORT entrada=" + _entradaPreco.ToString("F2") +
                      " stop=" + stopPrice.ToString("F2") +
                      " stopTicks=" + stopTicks.ToString("F0") + "t" +
                      " rangeWidth=" + rangeWidth.ToString("F0") + "t" +
                      " qtd=" + Contratos);
            }
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Gestao de saida — D1 / D2 / D3 / Trail

        private void GerenciarSaida()
        {
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                if (_fase == Fase.Executada)
                    ResetarFase();
                return;
            }

            int posQtd = Position.Quantity;

            // C2: aguarda fill completo
            if (!_fillCompleto)
            {
                if (posQtd >= Contratos)
                {
                    _fillCompleto = true;
                    _qtdInicial   = Contratos; // C1
                    if (ModoDebug)
                        Print("[Scalp20_34] fillCompleto posQtd=" + posQtd +
                              " _qtdInicial=" + _qtdInicial);
                }
                else
                {
                    if (ModoDebug)
                        Print("[Scalp20_34] Aguardando fill posQtd=" + posQtd + "/" + Contratos);
                    return;
                }
            }

            if (_qtdInicial == 0) _qtdInicial = Contratos;

            if (Position.MarketPosition == MarketPosition.Long)
                GerenciarSaidaLong(posQtd);
            else if (Position.MarketPosition == MarketPosition.Short)
                GerenciarSaidaShort(posQtd);
        }

        // ── Long ─────────────────────────────────────────────────────────────
        private void GerenciarSaidaLong(int posQtd)
        {
            double lucroTicks = (Close[0] - _entradaPreco) / TickSize;

            // D1
            if (!_d1Executada && lucroTicks >= D1AlvoTicks)
            {
                int qtdD1 = Math.Min(
                    Math.Max(1, (int)Math.Round(_qtdInicial * D1PctContratos / 100.0)),
                    posQtd);

                // ExitLong ANTES do SetStopLoss (Bug1 fix v3.3)
                ExitLong(qtdD1, "D1_Long", "ScalpLong");

                // v3.4 fix desalavancagem: residual explícito no SetStopLoss
                // Sem isso o NT8 emite o stop para a quantidade original da entrada,
                // e ao ser atingido fecha o residual + inverte a posição.
                int residualD1 = posQtd - qtdD1;

                // Clamp: BE nunca regride abaixo do stop inicial
                double bePrice = Math.Max(
                    _entradaPreco + TickSize * StopBufferTicks,
                    _stopInicial);

                if (residualD1 > 0)
                    SetStopLoss("ScalpLong", CalculationMode.Price, bePrice, false, residualD1);

                _d1Executada = true;
                _breakEven   = true;

                if (ModoDebug)
                    Print("[Scalp20_34] D1 Long qtd=" + qtdD1 +
                          " lucro=" + lucroTicks.ToString("F0") + "t" +
                          " BE=" + bePrice.ToString("F2") +
                          " residual=" + residualD1 + "ct");

                posQtd = residualD1; // atualiza qtd local para fases seguintes no mesmo bar
            }

            // D2
            if (_d1Executada && !_d2Executada && UsarD2 && posQtd > 0)
            {
                bool disparoTicks    = lucroTicks >= D2AlvoTicks;
                bool disparoCessacao = UsarD2Dinamica && NaZonaMediaLong() &&
                                       ScoreCessacao() >= D2ScoreMinimo;

                if (disparoTicks || disparoCessacao)
                {
                    int qtdD2 = Math.Min(
                        Math.Max(1, (int)Math.Round(_qtdInicial * 20.0 / 100.0)),
                        posQtd);

                    _precoD2 = Close[0];
                    // ExitLong antes do SetStopLoss (Bug1 fix v3.3)
                    ExitLong(qtdD2, "D2_Long", "ScalpLong");

                    // v3.4 fix desalavancagem: residual explícito no SetStopLoss
                    int residualD2 = posQtd - qtdD2;
                    double stopD2  = _precoD2 - TickSize * D2StopAposSaidaTicks;

                    if (residualD2 > 0)
                        SetStopLoss("ScalpLong", CalculationMode.Price, stopD2, false, residualD2);

                    _d2Executada = true;

                    if (ModoDebug)
                        Print("[Scalp20_34] D2 Long qtd=" + qtdD2 +
                              " lucro=" + lucroTicks.ToString("F0") + "t" +
                              " precoD2=" + _precoD2.ToString("F2") +
                              " stopD2=" + stopD2.ToString("F2") +
                              " residual=" + residualD2 + "ct");

                    posQtd = residualD2; // atualiza qtd local para fases seguintes no mesmo bar
                }
            }

            // D3
            if (_d1Executada && !_d3Executada && UsarD3 && posQtd > 0)
            {
                bool d2ok = !UsarD2 || _d2Executada;
                if (d2ok && lucroTicks >= D3AlvoTicks)
                {
                    int qtdD3 = Math.Min(
                        Math.Max(1, (int)Math.Round(_qtdInicial * 10.0 / 100.0)),
                        posQtd);

                    _precoD3 = Close[0];
                    // ExitLong antes do SetStopLoss (Bug1 fix v3.3)
                    ExitLong(qtdD3, "D3_Long", "ScalpLong");

                    // v3.4 fix desalavancagem: residual explícito no SetStopLoss
                    int residualD3 = posQtd - qtdD3;
                    double stopD3  = _precoD3 - TickSize * D3StopAposSaidaTicks;

                    if (residualD3 > 0)
                        SetStopLoss("ScalpLong", CalculationMode.Price, stopD3, false, residualD3);

                    _d3Executada = true;
                    _emTrail     = true;

                    if (ModoDebug)
                        Print("[Scalp20_34] D3 Long qtd=" + qtdD3 +
                              " lucro=" + lucroTicks.ToString("F0") + "t" +
                              " residual=" + residualD3 + "ct → Trail ON");

                    posQtd = residualD3; // atualiza qtd local para o trail no mesmo bar
                }
            }

            // Trail — ativa no momento certo conforme fases habilitadas
            if (_d1Executada && !_emTrail && !UsarD2 && !UsarD3) _emTrail = true;
            if (_d2Executada && !_emTrail && !UsarD3)             _emTrail = true;

            if (_emTrail && posQtd > 0)
                SetTrailStop("ScalpLong", CalculationMode.Ticks, TrailAposD3Ticks, false);
        }

        // ── Short ────────────────────────────────────────────────────────────
        private void GerenciarSaidaShort(int posQtd)
        {
            double lucroTicks = (_entradaPreco - Close[0]) / TickSize;

            // D1
            if (!_d1Executada && lucroTicks >= D1AlvoTicks)
            {
                int qtdD1 = Math.Min(
                    Math.Max(1, (int)Math.Round(_qtdInicial * D1PctContratos / 100.0)),
                    posQtd);

                // ExitShort ANTES do SetStopLoss (Bug1 fix v3.3)
                ExitShort(qtdD1, "D1_Short", "ScalpShort");

                // v3.4 fix desalavancagem: residual explícito no SetStopLoss
                int residualD1 = posQtd - qtdD1;

                // Clamp: BE nunca avança acima do stop inicial (Short)
                double bePrice = Math.Min(
                    _entradaPreco - TickSize * StopBufferTicks,
                    _stopInicial);

                if (residualD1 > 0)
                    SetStopLoss("ScalpShort", CalculationMode.Price, bePrice, false, residualD1);

                _d1Executada = true;
                _breakEven   = true;

                if (ModoDebug)
                    Print("[Scalp20_34] D1 Short qtd=" + qtdD1 +
                          " lucro=" + lucroTicks.ToString("F0") + "t" +
                          " BE=" + bePrice.ToString("F2") +
                          " residual=" + residualD1 + "ct");

                posQtd = residualD1; // atualiza qtd local para fases seguintes no mesmo bar
            }

            // D2
            if (_d1Executada && !_d2Executada && UsarD2 && posQtd > 0)
            {
                if (lucroTicks >= D2AlvoTicks)
                {
                    int qtdD2 = Math.Min(
                        Math.Max(1, (int)Math.Round(_qtdInicial * 20.0 / 100.0)),
                        posQtd);

                    _precoD2 = Close[0];
                    // ExitShort antes do SetStopLoss (Bug1 fix v3.3)
                    ExitShort(qtdD2, "D2_Short", "ScalpShort");

                    // v3.4 fix desalavancagem: residual explícito no SetStopLoss
                    int residualD2 = posQtd - qtdD2;
                    double stopD2  = _precoD2 + TickSize * D2StopAposSaidaTicks;

                    if (residualD2 > 0)
                        SetStopLoss("ScalpShort", CalculationMode.Price, stopD2, false, residualD2);

                    _d2Executada = true;

                    if (ModoDebug)
                        Print("[Scalp20_34] D2 Short qtd=" + qtdD2 +
                              " lucro=" + lucroTicks.ToString("F0") + "t" +
                              " precoD2=" + _precoD2.ToString("F2") +
                              " stopD2=" + stopD2.ToString("F2") +
                              " residual=" + residualD2 + "ct");

                    posQtd = residualD2; // atualiza qtd local para fases seguintes no mesmo bar
                }
            }

            // D3
            if (_d1Executada && !_d3Executada && UsarD3 && posQtd > 0)
            {
                bool d2ok = !UsarD2 || _d2Executada;
                if (d2ok && lucroTicks >= D3AlvoTicks)
                {
                    int qtdD3 = Math.Min(
                        Math.Max(1, (int)Math.Round(_qtdInicial * 10.0 / 100.0)),
                        posQtd);

                    _precoD3 = Close[0];
                    // ExitShort antes do SetStopLoss (Bug1 fix v3.3)
                    ExitShort(qtdD3, "D3_Short", "ScalpShort");

                    // v3.4 fix desalavancagem: residual explícito no SetStopLoss
                    int residualD3 = posQtd - qtdD3;
                    double stopD3  = _precoD3 + TickSize * D3StopAposSaidaTicks;

                    if (residualD3 > 0)
                        SetStopLoss("ScalpShort", CalculationMode.Price, stopD3, false, residualD3);

                    _d3Executada = true;
                    _emTrail     = true;

                    if (ModoDebug)
                        Print("[Scalp20_34] D3 Short qtd=" + qtdD3 +
                              " lucro=" + lucroTicks.ToString("F0") + "t" +
                              " residual=" + residualD3 + "ct → Trail ON");

                    posQtd = residualD3; // atualiza qtd local para o trail no mesmo bar
                }
            }

            // Trail
            if (_d1Executada && !_emTrail && !UsarD2 && !UsarD3) _emTrail = true;
            if (_d2Executada && !_emTrail && !UsarD3)             _emTrail = true;

            if (_emTrail && posQtd > 0)
                SetTrailStop("ScalpShort", CalculationMode.Ticks, TrailAposD3Ticks, true);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region D2 Dinamica — zona MM200 + score de cessacao

        private bool NaZonaMediaLong()
        {
            double zona = _atr[0] * D2BufferATRFator;
            return Math.Abs(Close[0] - _mm200[0]) <= zona;
        }

        private int ScoreCessacao()
        {
            int score = 0;
            double mediaAbsDelta = MediaDeltaBuffer();
            double deltaAtual    = Math.Abs(CalcularDelta());
            if (mediaAbsDelta > 0 && deltaAtual < mediaAbsDelta * (D2DeltaQuedaPct / 100.0))
                score++;
            if (Volume[0] < _smaVol[0])
                score++;
            double rangeAtual = (High[0] - Low[0]) / TickSize;
            double atrTicks   = _atr[0] / TickSize;
            if (atrTicks > 0 && rangeAtual < atrTicks * D2RangeEncolheFator)
                score++;
            return score;
        }

        private void AtualizarDeltaBuffer()
        {
            if (_deltaBuffer == null) return;
            _deltaBuffer[_deltaBufferIdx % D2DeltaMediaBars] = Math.Abs(CalcularDelta());
            _deltaBufferIdx++;
            if (_deltaBufferIdx >= D2DeltaMediaBars)
                _deltaBufferCheio = true;
        }

        private double MediaDeltaBuffer()
        {
            if (_deltaBuffer == null) return 0;
            int count = _deltaBufferCheio ? D2DeltaMediaBars : _deltaBufferIdx;
            if (count == 0) return 0;
            double soma = 0;
            for (int i = 0; i < count; i++) soma += _deltaBuffer[i];
            return soma / count;
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Calculo de Stop

        private double CalcularStopLong()
        {
            double stop = 0;

            if (UsarStopRange)
                stop = EscolherMaisConservador(stop, _rangeLow - TickSize * StopBufferTicks, true);
            if (UsarStopSwing)
                stop = EscolherMaisConservador(stop, _swingLow - TickSize * StopBufferTicks, true);
            if (!UsarStopRange && !UsarStopSwing)
                stop = Close[0] - TickSize * StopInicialTicks;

            return stop;
        }

        private double CalcularStopShort()
        {
            double stop = 0;

            if (UsarStopRange)
                stop = EscolherMaisConservador(stop, _rangeHigh + TickSize * StopBufferTicks, false);
            if (UsarStopSwing)
                stop = EscolherMaisConservador(stop, _swingHigh + TickSize * StopBufferTicks, false);
            if (!UsarStopRange && !UsarStopSwing)
                stop = Close[0] + TickSize * StopInicialTicks;

            return stop;
        }

        private double EscolherMaisConservador(double atual, double candidato, bool isLong)
        {
            if (atual == 0) return candidato;
            return isLong ? Math.Min(atual, candidato) : Math.Max(atual, candidato);
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Delta / Agressao

        private double CalcularDelta()
        {
            if (ModoAgressao == ModoAgressaoEnum.VolumetricBars)
            {
                try
                {
                    var vb = Bars.BarsSeries.BarsType as
                        NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType;
                    if (vb != null)
                        return vb.Volumes[CurrentBar].BarDelta;
                }
                catch { }
            }
            return DeltaProxyCalc();
        }

        private double DeltaProxyCalc()
        {
            double range = High[0] - Low[0];
            if (range <= 0) return 0;
            double ratio = (Close[0] - Low[0]) / range;
            return (ratio * 2 - 1) * Volume[0];
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Utilitarios

        private void ResetarFase()
        {
            _fase            = Fase.Idle;
            _rangeHigh       = 0;
            _rangeLow        = 0;
            _swingHigh       = 0;
            _swingLow        = 0;
            _barConsolInicio = -1;
            _deltaAcum       = 0;
            _d1Executada     = false;
            _d2Executada     = false;
            _d3Executada     = false;
            _emTrail         = false;
            _breakEven       = false;
            _fillCompleto    = false;
            _stopInicial     = 0;
            _precoD2         = 0;
            _precoD3         = 0;
            _qtdInicial      = 0;
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────────
        #region Parametros (Properties)

        // 01 - Geral
        [NinjaScriptProperty]
        [Display(Name = "Compressao limite (ticks)", GroupName = "01 - Geral", Order = 1)]
        public int CompressaoLimiteTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Lookback consolidacao (barras)", GroupName = "01 - Geral", Order = 2)]
        public int ConsolidacaoLookback { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Lookback volume (barras)", GroupName = "01 - Geral", Order = 3)]
        public int VolumeLookback { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Modo agressao", GroupName = "01 - Geral", Order = 4)]
        public ModoAgressaoEnum ModoAgressao { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Delta minimo Long", GroupName = "01 - Geral", Order = 5)]
        public double DeltaMinimoLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Delta minimo Short (negativo)", GroupName = "01 - Geral", Order = 6)]
        public double DeltaMinimoShort { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Modo debug", GroupName = "01 - Geral", Order = 7)]
        public bool ModoDebug { get; set; }

        // 02 - Direcao
        [NinjaScriptProperty]
        [Display(Name = "Habilitar Long", GroupName = "02 - Direcao", Order = 1)]
        public bool HabilitarLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Habilitar Short", GroupName = "02 - Direcao", Order = 2)]
        public bool HabilitarShort { get; set; }

        // 03 - Gestao de Risco
        [NinjaScriptProperty]
        [Display(Name = "Usar Gestao de Risco", GroupName = "03 - Gestao de Risco", Order = 1)]
        public bool UsarGestaoRisco { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max Trades Por Dia", GroupName = "03 - Gestao de Risco", Order = 2)]
        public int MaxTradesPorDia { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max Stops Por Dia", GroupName = "03 - Gestao de Risco", Order = 3)]
        public int MaxStopsPorDia { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max Perda Por Dia ($)", GroupName = "03 - Gestao de Risco", Order = 4)]
        public double MaxPerdaPorDia { get; set; }

        // 05 - Gestao (unificado — estilo stg20com34)
        [NinjaScriptProperty]
        [Display(Name = "Contratos", GroupName = "05 — Gestao", Order = 1)]
        public int Contratos { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stop inicial (ticks)", GroupName = "05 — Gestao", Order = 2)]
        public int StopInicialTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stop pelo range da consolidacao", GroupName = "05 — Gestao", Order = 3)]
        public bool UsarStopRange { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stop pelo swing extremo", GroupName = "05 — Gestao", Order = 4)]
        public bool UsarStopSwing { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Buffer stop (ticks)", GroupName = "05 — Gestao", Order = 5)]
        public int StopBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "StopMax (ticks) — 0 = sem limite", GroupName = "05 — Gestao", Order = 6)]
        public int StopMaxTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "D1 Alvo (ticks)", GroupName = "05 — Gestao", Order = 7)]
        public int D1AlvoTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "D1 % Contratos (60)", GroupName = "05 — Gestao", Order = 8)]
        public int D1PctContratos { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Usar D2", GroupName = "05 — Gestao", Order = 9)]
        public bool UsarD2 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "D2 Alvo (ticks)", GroupName = "05 — Gestao", Order = 10)]
        public int D2AlvoTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "D2 Stop apos saida (ticks do preco D2)", GroupName = "05 — Gestao", Order = 11)]
        public int D2StopAposSaidaTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Usar D3", GroupName = "05 — Gestao", Order = 12)]
        public bool UsarD3 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "D3 Alvo (ticks)", GroupName = "05 — Gestao", Order = 13)]
        public int D3AlvoTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "D3 Stop apos saida (ticks do preco D3)", GroupName = "05 — Gestao", Order = 14)]
        public int D3StopAposSaidaTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trail apos D3 (ticks)", GroupName = "05 — Gestao", Order = 15)]
        public int TrailAposD3Ticks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trail step (ticks)", GroupName = "05 — Gestao", Order = 16)]
        public int TrailStepTicks { get; set; }

        // 06 - Score Exaustao (D2 dinamica)
        [NinjaScriptProperty]
        [Display(Name = "Usar D2 dinamica (MM200 + cessacao)", GroupName = "06 - Score Exaustao", Order = 1)]
        public bool UsarD2Dinamica { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "D2 MM200 periodo", GroupName = "06 - Score Exaustao", Order = 2)]
        public int D2MM200Periodo { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "D2 Buffer zona (ATR x fator)", GroupName = "06 - Score Exaustao", Order = 3)]
        public double D2BufferATRFator { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "D2 ATR periodo", GroupName = "06 - Score Exaustao", Order = 4)]
        public int D2ATRPeriodo { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "D2 Delta barras para media", GroupName = "06 - Score Exaustao", Order = 5)]
        public int D2DeltaMediaBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "D2 Delta queda % para sinal (ex: 25)", GroupName = "06 - Score Exaustao", Order = 6)]
        public double D2DeltaQuedaPct { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "D2 Range fator ATR encolhimento (ex: 0.60)", GroupName = "06 - Score Exaustao", Order = 7)]
        public double D2RangeEncolheFator { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "D2 Score minimo para disparar (1-3)", GroupName = "06 - Score Exaustao", Order = 8)]
        public int D2ScoreMinimo { get; set; }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
	}
}

#endregion
