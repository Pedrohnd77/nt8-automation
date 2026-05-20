#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

/*
 * stg20com34 — v1.16
 * Continuidade com VP Segmentado — Idom
 * 18/05/2026
 *
 * LOGICA:
 * 1. Consolidacao: preco dentro das bandas da SMA20(5m) por N barras
 * 2. Expulsao CIMA  -> setup Long  (entra a favor apos retorno em V)
 * 2. Expulsao BAIXO -> setup Short (entra a favor apos retorno em V)
 * 3. Retorno em V: preco volta para a SMA20(5m)
 * 4. Cruzamento SMA34(1m): confirmacao final + RSI + Delta
 * 5. Entrada a mercado
 *
 * DESALAVANCAGEM PROGRESSIVA (D1 / D2 / D3):
 * D1 — alvo D1Ticks ou score exaustao >= limiar → sai 60% → stop para Math.Max(BE, _stopInicial)
 * D2 — alvo D2Ticks ou score exaustao >= limiar → sai 20% total (50% saldo D1) → stop para precoD2 - D2StopTicks
 * D3 — alvo D3Ticks ou score exaustao >= limiar → sai 10% total (50% saldo D2) → stop para precoD3 - D3StopTicks
 * Apos D3 → SetTrailStop nativo (TrailTicks)
 *
 * PROPORCOES: D1/D2/D3 calculadas sobre _qtdInicial (fixado no fill completo),
 * nao sobre saldo remanescente. Anti-overshooting em toda saida.
 *
 * SCORE DE EXAUSTAO (Opcao C — hibrido ticks + fluxo):
 * Volume  (peso 3.0): media(Vol, NBarrasVolume) < SMA(Vol,10) * 0.7
 * CumDelta(peso 2.0): delta cumulativo caindo por NBarrasDelta barras consecutivas
 * RSI 5m  (peso 0.75): RSI5m caindo por NBarrasRSI barras E abaixo de 60/acima de 40
 * RSI 1m  (peso 0.25): RSI1m caindo por NBarrasRSI barras E abaixo de 55/acima de 45
 * Toggle UsarScoreExaustao (ON/OFF) — padrao OFF ate calibracao
 * Dispara saida antecipada se score >= ScoreExaustaoMinimo
 *
 * CORRECOES v1.11:
 * - Anti-overshooting: todas as saidas clampam com Math.Min(qtd, Position.Quantity)
 * - Stop recalibrado apos cada desalavancagem (nao "vira" a posicao)
 * - TrailPassoTicks removido (SetTrailStop nativo — equivalente em OnBarClose)
 * - pnlDolar corrigido: MNQ = $0.50/tick
 *
 * VERSAO REALTIME v1.16 (18/05/2026) — config validada por backtest OOS:
 * Parâmetros calibrados após análise Set/25–Abr/26 (1189 trades):
 *   Range Max Consolidacao : 40t  (era 80t)
 *   D1 Alvo               : 80t  (era 120t)
 *   D2 Stop apos saida     : 20t  (era 40t)
 *   Filtro Tendencia ON    : SMA20 > SMA200 (5m) — mantido
 *   Filtro RSI5m cruzamento: OFF  — confirmado prejudicial
 *   Tolerancia RetornoV    : 120t — mantida
 *
 * Resultados OOS validados:
 *   Set/25–Abr/26 (8m): FL=1,04 | WR=44,6% | DD=-$6.473 | +$319/mês
 *   Jan/26–Abr/26 (4m): FL=1,20 | WR=46,9% | DD=-$2.742 | +$1.397/mês
 *   Set/25–Dez/25 (4m): FL=0,91 | WR=42,6% — regime choppy pós-eleição EUA
 *
 * NOVO v1.16: log Verif1m ampliado com tend, sma20_5m, sma200_5m
 *   Diagnóstico: filtro tendência bloqueava silenciosamente (não logado em v1.14)
 *   Agora visível: tend:True/False | sma20_5m | sma200_5m em cada barra
 *
 * CORRECOES v1.16 (bloqueios operacionais — diagnóstico 18/05/2026):
 * - FIX 1 (crítico): rsiFavoravel5m removido de VerificarCruzamento1m()
 *     RSI5m bloqueava entradas legítimas (ex: RSI5m=41.8 < 45 em 18/05 07:47)
 *     RSI5m já cumpre papel no filtro de tendência (UsarFiltroTendencia)
 *     Toggle UsarFiltroRSI5m adicionado (padrão OFF) para calibração futura
 * - FIX 2 (importante): ToleranciRetornoVTicks — parâmetro próprio para RetornoV
 *     Antes: usava RangeConsolidacaoTicks (80t) como tolerância do RetornoV
 *     Resultado: RetornoV reconhecido 48 min após expulsão em 18/05
 *     Agora: tolerância independente, padrão 120t — mais ampla que a banda de consolid.
 * - FIX 3 (melhoria): Toggle UsarFiltroRSI5m no cruzamento
 *     Permite habilitar RSI5m como filtro adicional no cruzamento para calibração
 *     Padrão OFF — mantém o comportamento corrigido do Fix 1
 *
 * CORRECOES v1.13 (checklist modelo desalavancagem):
 * - _qtdInicial = Contratos (fixo) — nunca posQtd (C1 do Scalp20_34)
 * - Guard Flat+Qty==0 em ExecutarEntrada() — bloqueia entrada sobre resíduo
 * - OnExecutionUpdate detecta "StopCancelClose"/"Zerar" → ResetarFSM()
 *
 * CORRECOES v1.12 (modelo desalavancagem padrao):
 * - _fillCompleto guard: _qtdInicial fixado apenas quando Position.Quantity >= Contratos
 * - _qtdInicial fallback: garantido antes de D1 (nunca zero)
 * - _stopInicial: D1 stop = Math.Max(precoEntrada, _stopInicial) Long / Math.Min Short
 * - UsarScoreExaustao toggle (ON/OFF) adicionado ao painel
 * - StopMaxTicks: bloqueia entrada se stop calculado excede limite configuravel
 * - _bloqueioLogado: print de bloqueio diario suprimido apos primeira vez
 *
 * INSTALACAO: Documents\NinjaTrader 8\bin\Custom\Strategies\stg20com34.cs
 */

namespace NinjaTrader.NinjaScript.Strategies
{
    public class stg20com34 : Strategy
    {
        private enum EstadoFSM
        {
            Aguardando,
            Consolidando,
            Expulsao,
            RetornoV
        }

        private EstadoFSM estado = EstadoFSM.Aguardando;

        // ── Indicadores ──────────────────────────────────────────────────────
        private SMA sma20_5m;
        private SMA sma200_5m;
        private SMA sma34_1m;
        private SMA smaVol10;
        private RSI rsi5m;
        private RSI rsi1m;

        // ── Estado FSM ───────────────────────────────────────────────────────
        private double rangeHigh        = 0;
        private double rangeLow         = 0;
        private double pocNivel         = 0;
        private int    barExpulsao      = 0;
        private int    barRetornoV      = 0;
        private bool   cruzou5m         = false;
        private bool   direcaoLong      = false;
        private int    barsConsolidando = 0;

        // ── Gestão de posição ────────────────────────────────────────────────
        private bool   d1Executada      = false;
        private bool   d2Executada      = false;
        private bool   d3Executada      = false;
        private bool   trailAtivo       = false;

        // Fill guard e quantidade inicial (modelo desalavancagem padrão)
        private bool   _fillCompleto    = false;
        private int    _qtdInicial      = 0;
        private double _stopInicial     = 0;

        // Stop progressivo — preço no momento de cada desalavancagem
        private double precoEntrada     = 0;
        private double precoD2          = 0;
        private double precoD3          = 0;

        // Score de exaustão — delta cumulativo
        private double deltaCumAtual    = 0;
        private double deltaCumAnterior = 0;
        private int    barrasDelCaindo  = 0;
        private double deltaCumPico     = 0;

        // ── Controle diário ──────────────────────────────────────────────────
        private int    diaAtual         = -1;
        private int    tradesHoje       = 0;
        private int    stopsHoje        = 0;
        private double perdaHoje        = 0;
        private bool   bloqueadoHoje    = false;
        private bool   _bloqueioLogado  = false;

        // ── VP simulado ──────────────────────────────────────────────────────
        private double[] vpVolume;
        private double   vpMinGlobal;
        private double   vpMaxGlobal;

        // ── Constante tick value MNQ ─────────────────────────────────────────
        private const double TickValueMNQ = 0.50; // $0.50 por tick

        #region OnStateChange
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Continuidade com VP Segmentado — Léo Molini (Dolarize)";
                Name        = "stg20com34_v115";
                Calculate   = Calculate.OnBarClose;

                HabilitarLong  = true;
                HabilitarShort = true;

                // Consolidação
                RangeConsolidacaoTicks = 40;  // v1.16: 80→40 (backtest OOS Set/25–Abr/26)
                ConsolidacaoLookback   = 15;
                MinBarsConsolidando    = 5;

                // Volume Profile
                UsarVPSegmentado = true;
                LookbackVP       = 30;
                FaixasVP         = 20;

                // Filtros de entrada
                VolumeMultiplier    = 1.1;
                DeltaMinimo         = 0;
                RSIFiltroMinimo     = 45;
                UsarFiltroTendencia = true;
                UsarFiltroRSI5m     = false;  // FIX 3: toggle RSI5m no cruzamento — OFF = comportamento corrigido

                // FIX 2: tolerância RetornoV independente da banda de consolidação
                // Padrão 120t — mais amplo que RangeConsolidacaoTicks (80t)
                // Permite reconhecer RetornoV mesmo quando preço corrige mais fundo
                ToleranciRetornoVTicks = 120;

                // Gestão — tamanho e stop
                Contratos   = 1;
                StopTicks   = 40;

                // Desalavancagem progressiva
                D1Ticks      = 80;   // v1.16: 120→80 (backtest OOS Set/25–Abr/26)
                D1Percentual = 60;
                D2Ticks      = 200;
                D2StopTicks  = 20;   // v1.16: 40→20 (stop = precoD2 - D2StopTicks)
                D3Ticks      = 280;
                D3StopTicks  = 20;   // stop = precoD3 - D3StopTicks

                // Trail após D3
                TrailTicks      = 20;

                // Gestão diária
                MaxTradesPorDia = 10;
                MaxStopsPorDia  = 3;
                MaxPerdaPorDia  = 500.0;

                // Score de exaustão
                UsarScoreExaustao   = false;
                NBarrasVolume       = 3;
                NBarrasDelta        = 2;
                NBarrasRSI          = 2;
                ScoreExaustaoMinimo = 4.0;

                // Stop máximo por entrada (0 = desabilitado)
                StopMaxTicks = 0;

                // Diagnóstico
                ModoDebug = false;

                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                BarsRequiredToTrade          = 40;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(Data.BarsPeriodType.Minute, 5);
            }
            else if (State == State.DataLoaded)
            {
                sma34_1m  = SMA(BarsArray[0], 34);
                smaVol10  = SMA(Volume,        10);
                rsi1m     = RSI(BarsArray[0], 14, 3);
                sma20_5m  = SMA(BarsArray[1], 20);
                sma200_5m = SMA(BarsArray[1], 200);
                rsi5m     = RSI(BarsArray[1], 14, 3);
                vpVolume  = new double[FaixasVP];
            }
        }
        #endregion

        #region OnBarUpdate
        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;
            if (CurrentBar < BarsRequiredToTrade) return;
            if (BarsArray[1].Count < 25) return;

            AtualizarControleDiario();
            if (bloqueadoHoje) return;

            // Atualiza delta cumulativo sempre (mesmo com posição aberta)
            AtualizarDeltaCumulativo();

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                GerenciarPosicao();
                return;
            }

            if (UsarVPSegmentado)
                AtualizarVPSegmentado();

            if (ModoDebug && CurrentBar % 100 == 0)
                Print(Time[0] + " | Estado: " + estado + " | BarsConsol: " + barsConsolidando);

            switch (estado)
            {
                case EstadoFSM.Aguardando:
                    VerificarConsolidacao();
                    break;

                case EstadoFSM.Consolidando:
                    VerificarConsolidacao();
                    VerificarExpulsao();
                    break;

                case EstadoFSM.Expulsao:
                    VerificarRetornoV();
                    if (CurrentBar - barExpulsao > 60)
                    {
                        if (ModoDebug) Print(Time[0] + " | Timeout Expulsao — resetando");
                        ResetarFSM();
                    }
                    break;

                case EstadoFSM.RetornoV:
                    VerificarCruzamento1m();
                    if (CurrentBar - barRetornoV > 80)
                    {
                        if (ModoDebug) Print(Time[0] + " | Timeout RetornoV — resetando");
                        ResetarFSM();
                    }
                    break;
            }
        }
        #endregion

        #region FSM — Consolidação
        private void VerificarConsolidacao()
        {
            if (CurrentBar < ConsolidacaoLookback + 2) return;
            if (BarsArray[1].Count < 3) return;

            double sma5m = sma20_5m[0];
            double banda = RangeConsolidacaoTicks * TickSize;
            bool   perto = Math.Abs(Close[0] - sma5m) <= banda;

            double maxH = double.MinValue;
            double minL = double.MaxValue;
            for (int i = 0; i < ConsolidacaoLookback; i++)
            {
                maxH = Math.Max(maxH, High[i]);
                minL = Math.Min(minL, Low[i]);
            }

            if (perto)
            {
                barsConsolidando++;
                rangeHigh = maxH;
                rangeLow  = minL;

                if (UsarVPSegmentado)
                    pocNivel = CalcularPOC();

                if (estado == EstadoFSM.Aguardando && barsConsolidando >= MinBarsConsolidando)
                {
                    estado = EstadoFSM.Consolidando;
                    if (ModoDebug) Print(Time[0]
                        + " | CONSOLIDANDO"
                        + " | dist SMA20:" + (Math.Abs(Close[0] - sma5m) / TickSize).ToString("F0") + "t"
                        + " | Range:" + ((maxH - minL) / TickSize).ToString("F0") + "t"
                        + " | Bars:" + barsConsolidando);
                    Draw.Rectangle(this, "Range_" + CurrentBar, false,
                        ConsolidacaoLookback, rangeHigh, 0, rangeLow,
                        Brushes.Transparent, Brushes.Yellow, 15);
                }
            }
            else
            {
                barsConsolidando = 0;
                if (estado == EstadoFSM.Consolidando)
                {
                    if (ModoDebug) Print(Time[0]
                        + " | Consolidacao perdida"
                        + " | dist SMA20:" + (Math.Abs(Close[0] - sma5m) / TickSize).ToString("F0") + "t"
                        + " | banda:" + RangeConsolidacaoTicks + "t");
                    ResetarFSM();
                }
            }
        }
        #endregion

        #region FSM — Expulsão
        private void VerificarExpulsao()
        {
            double volMedio = SMA(Volume, 10)[0];
            bool   volumeOk = Volume[0] > volMedio * VolumeMultiplier;

            if (HabilitarLong && Close[0] > rangeHigh && volumeOk)
            {
                estado      = EstadoFSM.Expulsao;
                direcaoLong = true;
                barExpulsao = CurrentBar;
                if (ModoDebug) Print(Time[0] + " | EXPULSAO CIMA (setup Long) | Close: " + Close[0]);
                Draw.ArrowUp(this, "Exp_" + CurrentBar, false,
                    0, Low[0] - TickSize * 3, Brushes.Lime);
            }
            else if (HabilitarShort && Close[0] < rangeLow && volumeOk)
            {
                estado      = EstadoFSM.Expulsao;
                direcaoLong = false;
                barExpulsao = CurrentBar;
                if (ModoDebug) Print(Time[0] + " | EXPULSAO BAIXO (setup Short) | Close: " + Close[0]);
                Draw.ArrowDown(this, "Exp_" + CurrentBar, false,
                    0, High[0] + TickSize * 3, Brushes.OrangeRed);
            }
        }
        #endregion

        #region FSM — Retorno em V
        private void VerificarRetornoV()
        {
            double sma5m = sma20_5m[0];
            double tol   = ToleranciRetornoVTicks * TickSize;  // FIX 2: parâmetro próprio

            if (direcaoLong && Close[0] >= sma5m - tol)
            {
                estado      = EstadoFSM.RetornoV;
                barRetornoV = CurrentBar;
                cruzou5m    = false;
                if (ModoDebug) Print(Time[0] + " | RETORNO V Long | Close:" + Close[0] + " SMA20:" + sma5m.ToString("F2"));
                Draw.TriangleUp(this, "RV_" + CurrentBar, false,
                    0, Low[0] - TickSize * 2, Brushes.Cyan);
            }
            else if (!direcaoLong && Close[0] <= sma5m + tol)
            {
                estado      = EstadoFSM.RetornoV;
                barRetornoV = CurrentBar;
                cruzou5m    = false;
                if (ModoDebug) Print(Time[0] + " | RETORNO V Short | Close:" + Close[0] + " SMA20:" + sma5m.ToString("F2"));
                Draw.TriangleDown(this, "RV_" + CurrentBar, false,
                    0, High[0] + TickSize * 2, Brushes.Cyan);
            }
        }
        #endregion

        #region FSM — Cruzamento SMA34 (1m) → ENTRADA
        private void VerificarCruzamento1m()
        {
            if (CurrentBar < 2) return;

            double sma34Atual    = sma34_1m[0];
            double sma34Anterior = sma34_1m[1];

            double deltaProxy    = (Close[0] - Open[0]) / TickSize;
            bool deltaFavoravel  = direcaoLong
                ? deltaProxy >= DeltaMinimo
                : deltaProxy <= -DeltaMinimo;

            bool rsiFavoravel1m = direcaoLong
                ? rsi1m[0] > RSIFiltroMinimo
                : rsi1m[0] < (100 - RSIFiltroMinimo);

            // FIX 1: rsiFavoravel5m removido da condição de entrada
            // RSI5m bloqueava cruzamentos legítimos (ex: 18/05 07:47 — RSI5m=41.8 < 45)
            // O RSI5m já está implícito no filtro de tendência (SMA20 > SMA200)
            // FIX 3: toggle UsarFiltroRSI5m — padrão OFF, habilitável para calibração
            bool rsiFavoravel5m = !UsarFiltroRSI5m || (direcaoLong
                ? rsi5m[0] > RSIFiltroMinimo
                : rsi5m[0] < (100 - RSIFiltroMinimo));

            bool tendenciaLong  = !UsarFiltroTendencia || sma20_5m[0] > sma200_5m[0];
            bool tendenciaShort = !UsarFiltroTendencia || sma20_5m[0] < sma200_5m[0];

            if (ModoDebug)
                Print(Time[0] + " | Verif1m"
                    + " | dir:" + (direcaoLong ? "L" : "S")
                    + " | close:" + Close[0].ToString("F2")
                    + " | sma34:" + sma34Atual.ToString("F2")
                    + " | delta:" + deltaProxy.ToString("F0") + "t"
                    + " | rsi1m:" + rsi1m[0].ToString("F1")
                    + " | rsi5m:" + rsi5m[0].ToString("F1")
                    + " | rsi5m_ok:" + rsiFavoravel5m
                    + " | tend:" + (direcaoLong ? tendenciaLong : tendenciaShort)
                    + " | sma20_5m:" + sma20_5m[0].ToString("F2")
                    + " | sma200_5m:" + sma200_5m[0].ToString("F2"));

            if (direcaoLong
                && Close[1] < sma34Anterior
                && Close[0] > sma34Atual
                && deltaFavoravel
                && rsiFavoravel1m
                && rsiFavoravel5m
                && tendenciaLong)
            {
                ExecutarEntrada(true);
            }
            else if (!direcaoLong
                && Close[1] > sma34Anterior
                && Close[0] < sma34Atual
                && deltaFavoravel
                && rsiFavoravel1m
                && rsiFavoravel5m
                && tendenciaShort)
            {
                ExecutarEntrada(false);
            }
        }
        #endregion

        #region Entrada
        private void ExecutarEntrada(bool isLong)
        {
            // C2: guard duplo — bloqueia entrada sobre resíduo de posição inversa
            if (Position.MarketPosition != MarketPosition.Flat || Position.Quantity != 0)
            {
                if (ModoDebug) Print(Time[0] + " | BLOQUEADO entrada: posição não zerada (mp="
                    + Position.MarketPosition + " qty=" + Position.Quantity + ")");
                return;
            }

            // Reset gestão
            d1Executada      = false;
            d2Executada      = false;
            d3Executada      = false;
            trailAtivo       = false;
            precoEntrada     = Close[0];
            precoD2          = 0;
            precoD3          = 0;

            // Reset fill guard e qtd inicial
            _fillCompleto    = false;
            _qtdInicial      = 0;

            // Reset score exaustão
            deltaCumAtual    = 0;
            deltaCumAnterior = 0;
            deltaCumPico     = 0;
            barrasDelCaindo  = 0;

            tradesHoje++;

            if (isLong)
            {
                double stopPriceLong = Close[0] - StopTicks * TickSize;
                if (StopMaxTicks > 0)
                {
                    double stopTicksCalc = (Close[0] - stopPriceLong) / TickSize;
                    if (stopTicksCalc > StopMaxTicks)
                    {
                        if (ModoDebug) Print(Time[0] + " | BLOQUEADO: stop=" + stopTicksCalc.ToString("F0") + "t > StopMaxTicks=" + StopMaxTicks);
                        return;
                    }
                }
                _stopInicial = stopPriceLong;
                EnterLong(Contratos, "EntradaLong");
                SetStopLoss("EntradaLong", CalculationMode.Ticks, StopTicks, false);
                if (ModoDebug) Print(Time[0] + " | ★ ENTRADA LONG @ " + Close[0]
                    + " | Contratos: " + Contratos
                    + " | Stop: " + StopTicks + "t");
                Draw.ArrowUp(this, "Entrada_" + CurrentBar, false,
                    0, Low[0] - TickSize * 6, Brushes.LimeGreen);
            }
            else
            {
                double stopPriceShort = Close[0] + StopTicks * TickSize;
                if (StopMaxTicks > 0)
                {
                    double stopTicksCalc = (stopPriceShort - Close[0]) / TickSize;
                    if (stopTicksCalc > StopMaxTicks)
                    {
                        if (ModoDebug) Print(Time[0] + " | BLOQUEADO: stop=" + stopTicksCalc.ToString("F0") + "t > StopMaxTicks=" + StopMaxTicks);
                        return;
                    }
                }
                _stopInicial = stopPriceShort;
                EnterShort(Contratos, "EntradaShort");
                SetStopLoss("EntradaShort", CalculationMode.Ticks, StopTicks, false);
                if (ModoDebug) Print(Time[0] + " | ★ ENTRADA SHORT @ " + Close[0]
                    + " | Contratos: " + Contratos
                    + " | Stop: " + StopTicks + "t");
                Draw.ArrowDown(this, "Entrada_" + CurrentBar, false,
                    0, High[0] + TickSize * 6, Brushes.Red);
            }

            ResetarFSM();
        }
        #endregion

        #region Gestão de Posição
        private void GerenciarPosicao()
        {
            if (Position.MarketPosition == MarketPosition.Flat) return;

            bool   isLong      = Position.MarketPosition == MarketPosition.Long;
            string nomeEntrada = isLong ? "EntradaLong" : "EntradaShort";

            // ── _fillCompleto guard ───────────────────────────────────────────
            // Aguarda fill completo antes de qualquer saída parcial
            int posQtd = Position.Quantity;
            if (!_fillCompleto)
            {
                if (posQtd >= Contratos)
                {
                    _qtdInicial  = Contratos; // C1: sempre o valor configurado, nunca posQtd
                    _fillCompleto = true;
                    if (ModoDebug) Print(Time[0] + " | Fill completo | _qtdInicial=" + _qtdInicial);
                }
                else
                {
                    if (ModoDebug) Print(Time[0] + " | Aguardando fill | pos=" + posQtd + " / " + Contratos);
                    return;
                }
            }

            // Fallback de segurança: _qtdInicial nunca deve ser zero
            if (_qtdInicial == 0) _qtdInicial = Contratos; // C1: fixo

            double pnlTicks = isLong
                ? (Close[0] - precoEntrada) / TickSize
                : (precoEntrada - Close[0]) / TickSize;

            // Atualiza perda do dia com valor correto MNQ ($0.50/tick)
            double pnlDolar = pnlTicks * TickValueMNQ * posQtd;
            if (pnlDolar < perdaHoje) perdaHoje = pnlDolar;

            // Score de exaustão (respeitando toggle)
            double score = (UsarScoreExaustao) ? CalcularScoreExaustao(isLong) : 0.0;

            if (ModoDebug)
                Print(Time[0]
                    + " | Gestao | pnl:" + pnlTicks.ToString("F0") + "t"
                    + " | pos:" + posQtd + " | qtdIni:" + _qtdInicial
                    + " | score:" + score.ToString("F2")
                    + " | d1:" + d1Executada + " d2:" + d2Executada + " d3:" + d3Executada
                    + " | trail:" + trailAtivo);

            // ── D1 ────────────────────────────────────────────────────────────
            if (!d1Executada && (pnlTicks >= D1Ticks || score >= ScoreExaustaoMinimo))
            {
                // Captura posição ANTES da saída (anti-race)
                int posQtdD1 = Position.Quantity;

                int qtdD1 = Math.Min(
                    (int)Math.Round(_qtdInicial * D1Percentual / 100.0),
                    posQtdD1);
                if (qtdD1 < 1) qtdD1 = 1;
                qtdD1 = Math.Min(qtdD1, posQtdD1);

                if (qtdD1 > 0)
                {
                    if (isLong) ExitLong (qtdD1, "D1", nomeEntrada);
                    else        ExitShort(qtdD1, "D1", nomeEntrada);
                }

                // Stop vai para Math.Max(BE, _stopInicial) Long / Math.Min Short
                double stopD1 = isLong
                    ? Math.Max(precoEntrada, _stopInicial)
                    : Math.Min(precoEntrada, _stopInicial);

                SetStopLoss(nomeEntrada, CalculationMode.Price, stopD1, false);

                d1Executada = true;

                if (ModoDebug) Print(Time[0]
                    + " | D1 executada | qtd:" + qtdD1
                    + " | pnl:" + pnlTicks.ToString("F0") + "t"
                    + " | score:" + score.ToString("F2")
                    + " | stop → " + stopD1.ToString("F2"));
                return;
            }

            // ── D2 ────────────────────────────────────────────────────────────
            if (d1Executada && !d2Executada && (pnlTicks >= D2Ticks || score >= ScoreExaustaoMinimo))
            {
                int posQtdD2 = Position.Quantity;

                int qtdD2 = Math.Min(
                    (int)Math.Round(_qtdInicial * 0.20),
                    posQtdD2);
                if (qtdD2 < 1) qtdD2 = 1;
                qtdD2 = Math.Min(qtdD2, posQtdD2);

                if (qtdD2 > 0)
                {
                    if (isLong) ExitLong (qtdD2, "D2", nomeEntrada);
                    else        ExitShort(qtdD2, "D2", nomeEntrada);
                }

                precoD2 = Close[0];

                double novoStopD2 = isLong
                    ? precoD2 - D2StopTicks * TickSize
                    : precoD2 + D2StopTicks * TickSize;

                SetStopLoss(nomeEntrada, CalculationMode.Price, novoStopD2, false);

                d2Executada = true;

                if (ModoDebug) Print(Time[0]
                    + " | D2 executada | qtd:" + qtdD2
                    + " | pnl:" + pnlTicks.ToString("F0") + "t"
                    + " | score:" + score.ToString("F2")
                    + " | stop → " + novoStopD2.ToString("F2"));
                return;
            }

            // ── D3 ────────────────────────────────────────────────────────────
            if (d2Executada && !d3Executada && (pnlTicks >= D3Ticks || score >= ScoreExaustaoMinimo))
            {
                int posQtdD3 = Position.Quantity;

                int qtdD3 = Math.Min(
                    (int)Math.Round(_qtdInicial * 0.10),
                    posQtdD3);
                if (qtdD3 < 1) qtdD3 = 1;
                qtdD3 = Math.Min(qtdD3, posQtdD3);

                if (qtdD3 > 0)
                {
                    if (isLong) ExitLong (qtdD3, "D3", nomeEntrada);
                    else        ExitShort(qtdD3, "D3", nomeEntrada);
                }

                precoD3 = Close[0];

                double novoStopD3 = isLong
                    ? precoD3 - D3StopTicks * TickSize
                    : precoD3 + D3StopTicks * TickSize;

                SetStopLoss(nomeEntrada, CalculationMode.Price, novoStopD3, false);

                d3Executada = true;
                trailAtivo  = true;

                if (ModoDebug) Print(Time[0]
                    + " | D3 executada | qtd:" + qtdD3
                    + " | pnl:" + pnlTicks.ToString("F0") + "t"
                    + " | score:" + score.ToString("F2")
                    + " | stop → " + novoStopD3.ToString("F2")
                    + " | trail ativado");
                return;
            }

            // ── Trail após D3 ─────────────────────────────────────────────────
            if (trailAtivo && d3Executada && Position.Quantity > 0)
            {
                if (isLong)
                    SetTrailStop(nomeEntrada, CalculationMode.Ticks, TrailTicks, false);
                else
                    SetTrailStop(nomeEntrada, CalculationMode.Ticks, TrailTicks, false);
            }
        }
        #endregion

        #region Score de Exaustão
        private double CalcularScoreExaustao(bool isLong)
        {
            double score = 0;

            // ── Vol (peso 3) ──────────────────────────────────────────────────
            // Media das ultimas NBarrasVolume < SMA(Vol,10) * 0.7
            if (CurrentBar >= NBarrasVolume + 10)
            {
                double somaVol = 0;
                for (int i = 0; i < NBarrasVolume; i++)
                    somaVol += Volume[i];
                double mediaVolRecente = somaVol / NBarrasVolume;
                double mediaVolHist    = smaVol10[0];

                if (mediaVolHist > 0 && mediaVolRecente < mediaVolHist * 0.7)
                    score += 3.0;

                if (ModoDebug)
                    Print(Time[0] + " | Score Vol | recente:" + mediaVolRecente.ToString("F0")
                        + " hist:" + mediaVolHist.ToString("F0")
                        + " ratio:" + (mediaVolRecente / mediaVolHist).ToString("F2"));
            }

            // ── CumulativeDelta (peso 2) ──────────────────────────────────────
            // Caindo por NBarrasDelta barras consecutivas desde o pico
            if (barrasDelCaindo >= NBarrasDelta)
                score += 2.0;

            // ── RSI 5m (peso 0.75) ────────────────────────────────────────────
            // Caindo por NBarrasRSI barras E abaixo do nível critico
            if (BarsArray[1].Count > NBarrasRSI + 1)
            {
                bool rsi5mCaindo = true;
                for (int i = 0; i < NBarrasRSI; i++)
                {
                    if (rsi5m[i] >= rsi5m[i + 1])
                    {
                        rsi5mCaindo = false;
                        break;
                    }
                }
                bool rsi5mNivel = isLong ? rsi5m[0] < 60 : rsi5m[0] > 40;
                if (rsi5mCaindo && rsi5mNivel)
                    score += 0.75;
            }

            // ── RSI 1m (peso 0.25) ────────────────────────────────────────────
            // Caindo por NBarrasRSI barras E abaixo do nível critico
            if (CurrentBar > NBarrasRSI + 1)
            {
                bool rsi1mCaindo = true;
                for (int i = 0; i < NBarrasRSI; i++)
                {
                    if (rsi1m[i] >= rsi1m[i + 1])
                    {
                        rsi1mCaindo = false;
                        break;
                    }
                }
                bool rsi1mNivel = isLong ? rsi1m[0] < 55 : rsi1m[0] > 45;
                if (rsi1mCaindo && rsi1mNivel)
                    score += 0.25;
            }

            return score;
        }

        private void AtualizarDeltaCumulativo()
        {
            // Acumula apenas quando há posição aberta
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                deltaCumAtual    = 0;
                deltaCumAnterior = 0;
                deltaCumPico     = 0;
                barrasDelCaindo  = 0;
                return;
            }

            double deltaBar = (Close[0] - Open[0]) / TickSize;
            deltaCumAnterior = deltaCumAtual;
            deltaCumAtual   += deltaBar;

            // Rastreia pico do delta cumulativo
            if (deltaCumAtual > deltaCumPico)
            {
                deltaCumPico    = deltaCumAtual;
                barrasDelCaindo = 0;
            }
            else if (deltaCumAtual < deltaCumAnterior)
            {
                barrasDelCaindo++;
            }
            else
            {
                barrasDelCaindo = 0;
            }

            if (ModoDebug)
                Print(Time[0] + " | DeltaCum:" + deltaCumAtual.ToString("F1")
                    + " | pico:" + deltaCumPico.ToString("F1")
                    + " | barrasCaindo:" + barrasDelCaindo);
        }
        #endregion

        #region Volume Profile Segmentado (simulado)
        private void AtualizarVPSegmentado()
        {
            if (CurrentBar < LookbackVP) return;

            vpMinGlobal = double.MaxValue;
            vpMaxGlobal = double.MinValue;

            for (int i = 0; i < LookbackVP; i++)
            {
                vpMinGlobal = Math.Min(vpMinGlobal, Low[i]);
                vpMaxGlobal = Math.Max(vpMaxGlobal, High[i]);
            }

            double faixaSize = (vpMaxGlobal - vpMinGlobal) / FaixasVP;
            if (faixaSize <= 0) return;

            for (int f = 0; f < FaixasVP; f++) vpVolume[f] = 0;

            for (int i = 0; i < LookbackVP; i++)
            {
                double barRange = High[i] - Low[i];
                if (barRange <= 0) continue;

                for (int f = 0; f < FaixasVP; f++)
                {
                    double fL = vpMinGlobal + f * faixaSize;
                    double fH = fL + faixaSize;
                    double oL = Math.Max(Low[i],  fL);
                    double oH = Math.Min(High[i], fH);
                    if (oH > oL)
                        vpVolume[f] += Volume[i] * (oH - oL) / barRange;
                }
            }
        }

        private double CalcularPOC()
        {
            if (vpVolume == null || FaixasVP <= 0) return (rangeHigh + rangeLow) / 2;
            double faixaSize = (vpMaxGlobal - vpMinGlobal) / FaixasVP;
            int    mf = 0;
            double mv = 0;
            for (int f = 0; f < FaixasVP; f++)
                if (vpVolume[f] > mv) { mv = vpVolume[f]; mf = f; }
            return vpMinGlobal + (mf + 0.5) * faixaSize;
        }
        #endregion

        #region Utilitários
        private void AtualizarControleDiario()
        {
            int diaHoje = Time[0].DayOfYear;
            if (diaHoje != diaAtual)
            {
                diaAtual        = diaHoje;
                tradesHoje      = 0;
                stopsHoje       = 0;
                perdaHoje       = 0;
                bloqueadoHoje   = false;
                _bloqueioLogado = false;
                if (ModoDebug) Print(Time[0] + " | Novo dia — contadores resetados");
            }

            if (!bloqueadoHoje)
            {
                if (tradesHoje >= MaxTradesPorDia)
                {
                    bloqueadoHoje = true;
                    if (ModoDebug && !_bloqueioLogado)
                    {
                        Print(Time[0] + " | BLOQUEADO: max trades/dia (" + tradesHoje + ")");
                        _bloqueioLogado = true;
                    }
                }
                else if (stopsHoje >= MaxStopsPorDia)
                {
                    bloqueadoHoje = true;
                    if (ModoDebug && !_bloqueioLogado)
                    {
                        Print(Time[0] + " | BLOQUEADO: max stops/dia (" + stopsHoje + ")");
                        _bloqueioLogado = true;
                    }
                }
                else if (perdaHoje <= -MaxPerdaPorDia)
                {
                    bloqueadoHoje = true;
                    if (ModoDebug && !_bloqueioLogado)
                    {
                        Print(Time[0] + " | BLOQUEADO: max perda/dia (" + perdaHoje.ToString("F2") + ")");
                        _bloqueioLogado = true;
                    }
                }
            }
        }

        private void ResetarFSM()
        {
            estado           = EstadoFSM.Aguardando;
            cruzou5m         = false;
            direcaoLong      = false;
            barExpulsao      = 0;
            barRetornoV      = 0;
            rangeHigh        = 0;
            rangeLow         = 0;
            pocNivel         = 0;
            barsConsolidando = 0;
        }
        #endregion

        #region OnExecutionUpdate
        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition,
            string orderId, DateTime time)
        {
            if (marketPosition == MarketPosition.Flat && SystemPerformance.AllTrades.Count > 0)
            {
                var ultimo = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];
                if (ultimo == null) return;

                double pnl = ultimo.ProfitCurrency;

                if (pnl < 0)
                    perdaHoje += pnl;

                if (execution.Name != null && execution.Name.ToLower().Contains("stop"))
                    stopsHoje++;

                if (ModoDebug)
                    Print(time
                        + " | Fechamento | PnL: " + pnl.ToString("F2")
                        + " | perdaHoje: " + perdaHoje.ToString("F2")
                        + " | stopsHoje: " + stopsHoje
                        + " | tradesHoje: " + tradesHoje);
            }

            // C3: detectar fechamento de resíduo por stop invertido → limpar FSM
            if (execution.Order != null &&
                (execution.Order.Name == "StopCancelClose" ||
                 execution.Order.Name == "Zerar"))
            {
                if (ModoDebug) Print(time + " | OnExec: " + execution.Order.Name + " → ResetarFSM forçado");
                ResetarFSM();
                _fillCompleto = false;
                _qtdInicial   = 0;
                d1Executada   = false;
                d2Executada   = false;
                d3Executada   = false;
                trailAtivo    = false;
            }
        }
        #endregion

        #region Parâmetros — Direção
        [NinjaScriptProperty]
        [Display(Name = "Habilitar Long", GroupName = "1 — Direcao", Order = 1)]
        public bool HabilitarLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Habilitar Short", GroupName = "1 — Direcao", Order = 2)]
        public bool HabilitarShort { get; set; }
        #endregion

        #region Parâmetros — Consolidação
        [NinjaScriptProperty]
        [Display(Name = "Range Max Consolidacao (ticks)", GroupName = "2 — Consolidacao", Order = 1)]
        public int RangeConsolidacaoTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Lookback Consolidacao (barras 1m)", GroupName = "2 — Consolidacao", Order = 2)]
        public int ConsolidacaoLookback { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Min Barras Consolidando", GroupName = "2 — Consolidacao", Order = 3)]
        public int MinBarsConsolidando { get; set; }
        #endregion

        #region Parâmetros — Volume Profile
        [NinjaScriptProperty]
        [Display(Name = "Usar VP Segmentado", GroupName = "3 — Volume Profile", Order = 1)]
        public bool UsarVPSegmentado { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Lookback VP (barras 1m)", GroupName = "3 — Volume Profile", Order = 2)]
        public int LookbackVP { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Faixas VP", GroupName = "3 — Volume Profile", Order = 3)]
        public int FaixasVP { get; set; }
        #endregion

        #region Parâmetros — Filtros de Entrada
        [NinjaScriptProperty]
        [Display(Name = "Volume Min (x media)", GroupName = "4 — Filtros", Order = 1)]
        public double VolumeMultiplier { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Delta Minimo (ticks, 0=off)", GroupName = "4 — Filtros", Order = 2)]
        public int DeltaMinimo { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RSI Filtro Minimo", GroupName = "4 — Filtros", Order = 3)]
        public int RSIFiltroMinimo { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filtro Tendencia SMA20>SMA200 (5m)", GroupName = "4 — Filtros", Order = 4)]
        public bool UsarFiltroTendencia { get; set; }

        // FIX 3: toggle RSI5m no cruzamento — OFF = comportamento corrigido (v1.16)
        [NinjaScriptProperty]
        [Display(Name = "Filtro RSI5m no cruzamento (OFF=v1.16)", GroupName = "4 — Filtros", Order = 5)]
        public bool UsarFiltroRSI5m { get; set; }

        // FIX 2: tolerância RetornoV independente da banda de consolidação (v1.16)
        [NinjaScriptProperty]
        [Display(Name = "Tolerancia RetornoV (ticks)", GroupName = "4 — Filtros", Order = 6)]
        public int ToleranciRetornoVTicks { get; set; }
        #endregion

        #region Parâmetros — Gestão
        [NinjaScriptProperty]
        [Display(Name = "Contratos", GroupName = "5 — Gestao", Order = 1)]
        public int Contratos { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stop inicial (ticks)", GroupName = "5 — Gestao", Order = 2)]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "D1 Alvo (ticks)", GroupName = "5 — Gestao", Order = 3)]
        public int D1Ticks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "D1 % Contratos (60)", GroupName = "5 — Gestao", Order = 4)]
        public int D1Percentual { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "D2 Alvo (ticks)", GroupName = "5 — Gestao", Order = 5)]
        public int D2Ticks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "D2 Stop apos saida (ticks do preco D2)", GroupName = "5 — Gestao", Order = 6)]
        public int D2StopTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "D3 Alvo (ticks)", GroupName = "5 — Gestao", Order = 7)]
        public int D3Ticks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "D3 Stop apos saida (ticks do preco D3)", GroupName = "5 — Gestao", Order = 8)]
        public int D3StopTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trail apos D3 (ticks)", GroupName = "5 — Gestao", Order = 9)]
        public int TrailTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max Trades Por Dia", GroupName = "5 — Gestao", Order = 10)]
        public int MaxTradesPorDia { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max Stops Por Dia", GroupName = "5 — Gestao", Order = 11)]
        public int MaxStopsPorDia { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max Perda Por Dia ($)", GroupName = "5 — Gestao", Order = 12)]
        public double MaxPerdaPorDia { get; set; }
        #endregion

        #region Parâmetros — Score de Exaustão
        [NinjaScriptProperty]
        [Display(Name = "Usar Score Exaustao (ON/OFF)", GroupName = "6 — Score Exaustao", Order = 0)]
        public bool UsarScoreExaustao { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "N Barras Volume (janela)", GroupName = "6 — Score Exaustao", Order = 1)]
        public int NBarrasVolume { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "N Barras Delta (consecutivas caindo)", GroupName = "6 — Score Exaustao", Order = 2)]
        public int NBarrasDelta { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "N Barras RSI (divergencia)", GroupName = "6 — Score Exaustao", Order = 3)]
        public int NBarrasRSI { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Score Exaustao Minimo (max=6)", GroupName = "6 — Score Exaustao", Order = 4)]
        public double ScoreExaustaoMinimo { get; set; }
        #endregion

        #region Parâmetros — Stop Máximo
        [NinjaScriptProperty]
        [Display(Name = "Stop Max Por Entrada (ticks, 0=off)", GroupName = "7 — Stop Maximo", Order = 1)]
        public int StopMaxTicks { get; set; }
        #endregion

        #region Parâmetros — Debug
        [NinjaScriptProperty]
        [Display(Name = "Modo Debug (Print)", GroupName = "7 — Debug", Order = 1)]
        public bool ModoDebug { get; set; }
        #endregion
    }
}
