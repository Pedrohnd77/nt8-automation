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
 * stg20com34 — v1.28
 * Continuidade com VP Segmentado — Idom
 * 25/05/2026
 *
 * CORRECOES v1.27 (bug bloqueante live sim — 22/05/2026):
 *
 *
 * CORRECAO v1.28 — FIX M — SetStopLoss após D1/D2/D3 diferido (BUG BLOQUEANTE LIVE):
 *   Causa raiz (25/05/2026 08:07): ExitLong(D1) + SetStopLoss() na mesma barra.
 *   NT8 ainda não processou o fill parcial quando SetStopLoss tenta modificar
 *   a ordem de stop original (ainda em voo) → broker rejeita "Não é possível
 *   alterar ordem" → estratégia cancela tudo e se mata.
 *   Solução: flags _stopD1Pendente/_stopD2Pendente/_stopD3Pendente + preço alvo.
 *   Após ExitLong/Short parcial, apenas setar a flag e o preço.
 *   Na barra SEGUINTE (OnBarUpdate), verificar flag e emitir SetStopLoss.
 *   Isso garante que o fill parcial foi confirmado antes da modificação do stop.
 *
 * FIX L — Trail pós-D3 substituído por gestão manual de stop em preço:
 *   Causa raiz confirmada em live sim 22/05 02:57:
 *   SetTrailStop('EntradaLong', Ticks, 20, false) referencia a ordem original
 *   de 7ct. NT8 cancela e reabre o stop para o tamanho da ordem de entrada,
 *   não para o residual real (1ct). Resultado: stop de Venda 4ct emitido,
 *   abrindo posição Short involuntária de 4ct após fechar o residual Long.
 *   Solução: trail manual com SetStopLoss(Price) calculado barra a barra.
 *   _trailHighMax (Long) / _trailLowMin (Short) rastreiam o melhor High/Low
 *   desde a ativação do trail (High[0]/Low[0] — não Close[0], para replicar
 *   o comportamento intrabar do SetTrailStop nativo em OnBarClose).
 *   A cada barra: novo High > _trailHighMax → atualiza referência e emite
 *   SetStopLoss(Price, highMax - TrailTicks*TickSize). Stop só avança,
 *   nunca recua. Backtest IS v1.27a (Close[0]): FL 1.61 / 907 trades.
 *   Ajuste v1.27b (High[0]/Low[0]): FL 1.61 / 907 trades — sem melhora.
 *   Ajuste v1.27c: _trailHighMax/_trailLowMin inicializados em precoD3
 *   no bloco D3 (não em High[0] da barra seguinte). Inicialização com
 *   High[0] podia ser menor que precoD3, stopando residual prematuramente.
 *
 * RESULTADO IS VALIDADO (Jan–Abr/26, MNQ 06-26, 7ct):
 *   FL 1.74 | Lucro +$12.234 | DD -$1.329 | +$3.109/mês | 850 trades
 *   Supera the best (v1.11): FL 1.69 | +$11.640 | DD -$1.501 | +$2.958/mês
 *   Todas correções operacionais de produção intactas (v1.13–v1.26)
 *
 * PARÂMETROS VALIDADOS (comparação sistemática — 21/05/2026):
 *   RangeConsolidacaoTicks : 40t
 *   ToleranciRetornoVTicks : 40t  (= RangeConsolidacaoTicks, como v1.11)
 *   UsarFiltroRSI5m        : true (ativo como v1.11 — filtra entradas de menor qualidade)
 *   UsarFiltroTendencia    : true
 *   StopTicks              : 40t
 *   D1Ticks / D1%          : 80t / 60%
 *   D2Ticks / D2Stop       : 200t / 20t
 *   D3Ticks / D3Stop       : 280t / 20t
 *   UsarScoreExaustao      : true  (Score=2 — saídas antecipadas por exaustão)
 *   ScoreExaustaoMinimo    : 2
 *   BarsRequiredToTrade    : 40    (warmup indicadores 1m)
 *   BarsArray[1].Count <   : 25    (warmup mínimo SMA 5m)
 * Continuidade com VP Segmentado — Idom
 * 21/05/2026
 *
 * CORRECOES v1.26 (comparação sistemática — avg tempo 3.67 min vs 10.82 min — 21/05/2026):
 *
 * FIX K — SetStopLoss restaurado em ExecutarEntrada() (CalculationMode.Ticks):
 *   Causa raiz confirmada por dados: avg tempo no mercado 3.67 min (v1.25)
 *   vs 10.82 min (v1.11 the best) — posições sendo stopadas na barra de entrada.
 *   Comparação linha a linha:
 *     v1.11: SetStopLoss(Ticks) em ExecutarEntrada() → proteção na mesma barra
 *     v1.21+: stop só emitido na barra seguinte via _fillCompleto → 1 barra sem stop
 *   Em backtest (OnBarClose), a barra da entrada já tem High/Low formados.
 *   Se o Low da barra de entrada estiver abaixo do stop, o v1.11 protege,
 *   o v1.21+ não — posição sobrevive 1 barra desprotegida e é stopada na próxima.
 *   Solução: SetStopLoss(Ticks) em ExecutarEntrada() (= v1.11).
 *   _fillCompleto mantido: quando confirmado, reemite SetStopLoss(Price) com
 *   _stopInicial — sobrescreve o stop de Ticks com ancoragem em preço absoluto.
 *   Em live: dupla proteção — stop imediato em Ticks + recalibração em Price pós-fill.
 *   Em backtest: comportamento idêntico ao v1.11 the best.
 *
 * CORRECOES v1.25 (comparação sistemática v1.11 vs v1.24 — 21/05/2026):
 *
 * FIX I — Ordem timeout/verificação no FSM revertida para pós-verificação:
 *   Problema (FIX v1.18 era excessivo em backtest): o timeout ANTES de
 *   VerificarRetornoV/VerificarCruzamento1m eliminava setups legítimos
 *   onde a barra exata do timeout coincidia com o RetornoV ou cruzamento.
 *   No v1.11 the best, o timeout era verificado DEPOIS — o setup era
 *   capturado primeiro, o timeout descartava apenas se não houvesse entrada.
 *   O FIX v1.18 foi introduzido para resolver o bug de barRetornoV=0 após
 *   ResetarFSM() dentro de ExecutarEntrada() — mas esse bug não existe em
 *   backtest, apenas em live com restart. Solução: timeout DEPOIS da
 *   verificação (comportamento the best), com guard barRetornoV/barExpulsao > 0
 *   mantido para evitar o falso trigger caso ResetarFSM() zere as vars.
 *
 * FIX J — UsarFiltroRSI5m default true:
 *   No v1.11 the best, rsiFavoravel5m estava sempre ativo na condição de
 *   entrada. Com UsarFiltroRSI5m=false no v1.16+, entradas de menor qualidade
 *   passavam. Confirmado por comparação sistemática: FL piora com RSI5m OFF.
 *   Restaurado default true — parâmetro mantido configurável.
 *
 * CORRECOES v1.24 (replicar the best — 21/05/2026):
 *
 * FIX H — ToleranciRetornoVTicks 120 → 40:
 *   No v1.11 the best, a tolerância do RetornoV era hardcoded como
 *   RangeConsolidacaoTicks * TickSize = 40t. Com ToleranciRetornoVTicks=120t
 *   o RetornoV aceita preço até 120t longe da SMA20 — entradas com
 *   menor qualidade de posicionamento relativo ao suporte.
 *   Solução: default ToleranciRetornoVTicks = 40 (igual ao the best).
 *   Parâmetro mantido configurável para calibração futura.
 *
 * CORRECOES v1.23 (diagnóstico galinha dos ovos de ouro — 21/05/2026):
 *
 * FIX G — BarsArray[1].Count < 200 → < 25:
 *   Causa raiz confirmada por comparação direta v1.11 (FL 1.69, 848 trades)
 *   vs v1.21 (FL 0.94, 520 trades), parâmetros idênticos.
 *   O guard BarsArray[1].Count < 200 aguarda 200 barras de 5m = ~16h de mercado
 *   = ~1000 barras de 1m antes de liberar qualquer entrada. Isso descarta
 *   todo o início de Jan/26 (regime direcional de alta) e reduz o universo
 *   de trades em 40%. O filtro era excessivo: a SMA200(5m) com 25 barras
 *   de 5m já tem valor válido para o filtro de tendência (sma20 vs sma200),
 *   mesmo que não esteja no valor "estabilizado" de longo prazo.
 *   Solução: BarsArray[1].Count < 25 (reproduz comportamento do v1.11 the best).
 *   BarsRequiredToTrade mantido em 40 (warmup indicadores 1m).
 *   Todas as correções operacionais v1.13–v1.21 (FIX A/B/C/D/E) intactas.
 *
 * CORRECOES v1.21 (diagnóstico log IS Jan–Abr/26 — 21/05/2026):
 *
 * FIX D — BarsRequiredToTrade 40 → 200:
 *   Problema: SMA200(5m) precisa de 200 barras para estabilizar (~16h de mercado).
 *   Com BarsRequiredToTrade=40, o backtest iniciava entradas antes do warmup completo.
 *   Log confirmou sma20_5m == sma200_5m nas primeiras horas — filtro de tendência
 *   retornava tend:True incorretamente, liberando entradas sem filtro real.
 *   Solução: BarsRequiredToTrade=200 + guard BarsArray[1].Count < 200 no OnBarUpdate.
 *
 * FIX E — Remoção do painel visual (prematuro):
 *   DesenharPainel(), _lastPainelBar, MostrarPainel removidos.
 *   Painel só será útil em live sim monitorado — não agora.
 *
 * NOVO v1.20 — Painel visual (revertido em v1.21)
 *
 * CORRECOES v1.19 (auditoria operacional — 21/05/2026):
 *
 * FIX A — Restart Recovery (BUG OPERACIONAL CONFIRMADO):
 *   Problema: MaxRestarts reiniciava a estratégia sobre posição aberta herdada.
 *   Com _fillCompleto=false e posQtd < Contratos, GerenciarPosicao() ficava em
 *   loop eterno de "Aguardando fill" — posição desprotegida até ExitOnSessionClose.
 *   Evidência: log 21/05 06:01 "AccountPosition=MNQ 06-26 3L" (29 min sem stop gerenciado).
 *   Solução: novo bloco de recovery em GerenciarPosicao() detecta posição herdada
 *   (posQtd > 0 && posQtd < Contratos && !_fillCompleto), adota a quantidade real,
 *   recalcula _stopInicial baseado em Position.AveragePrice e emite SetStopLoss imediatamente.
 *   Flag _restartRecovery para identificar no log. precoEntrada corrigido para AveragePrice.
 *
 * FIX B — Fechamento externo não detectado (BUG LATENTE):
 *   Problema: saída manual (order-quick, Zerar externo, etc.) não redefinia _fillCompleto
 *   e flags D1/D2/D3, deixando FSM em estado inconsistente.
 *   Evidência: CSV 20/05 20:56 — 4ct fechados por "order-quick" sem reset da estratégia.
 *   Risco: reentrada indesejada na mesma janela RetornoV se SMA34 cruzasse novamente.
 *   Solução: novo bloco em OnExecutionUpdate detecta marketPosition==Flat quando
 *   _fillCompleto==true e não houve D1/D2/D3 pela estratégia (fechamento externo),
 *   ou quando houve desalavancagem mas a posição zerou por caminho não monitorado.
 *   Reseta _fillCompleto, _qtdInicial e flags D1/D2/D3. ResetarFSM() apenas se
 *   ainda não havia sido chamado por StopCancelClose/Zerar.
 *
 * FIX C — precoEntrada em modo recovery usa AveragePrice:
 *   Ao herdar posição, precoEntrada era 0 (nunca setado por ExecutarEntrada).
 *   pnlTicks ficava inválido. Corrigido: _restartRecovery flag garante que
 *   precoEntrada seja setado para Position.AveragePrice na primeira barra de gestão.
 *
 * CORRECAO v1.18 (bug crítico operacional — 19/05/2026):
 * - Timeout de RetornoV e Expulsao verificados ANTES de VerificarCruzamento1m/VerificarRetornoV
 * - Guard: timeout só dispara se barRetornoV > 0 / barExpulsao > 0
 * - Problema: ResetarFSM() zeraba barRetornoV=0 dentro de ExecutarEntrada()
 *   Ao checar CurrentBar - 0 > 80 depois, sempre verdadeiro → segundo ResetarFSM()
 *   tentava cancelar stop já ativo na conta Apex → NT8 desabilitava a estratégia
 * - SetStopLoss removido de ExecutarEntrada() — não emitir stop antes do fill completo
 * - Stop inicial agora emitido em GerenciarPosicao() dentro do bloco _fillCompleto
 * - CalculationMode.Ticks → CalculationMode.Price com _stopInicial (preço absoluto)
 *   Previne fragmentação de posição após saídas parciais (D1/D2/D3)
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
 * Apos D3 → Trail manual: SetStopLoss(Price) rastreando pico/vale barra a barra
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
        private bool   _fillCompleto      = false;
        private int    _qtdInicial        = 0;
        private double _stopInicial       = 0;

        // FIX A v1.19: flag de recovery após restart com posição herdada
        private bool   _restartRecovery   = false;

        // FIX L v1.27: trail manual pós-D3 (substitui SetTrailStop)
        private double _trailHighMax      = 0;  // Long: pico máximo desde trail ativado
        private double _trailLowMin       = 0;  // Short: vale mínimo desde trail ativado

        // FIX M: deferimento de SetStopLoss após saídas parciais D1/D2/D3
        private bool   _stopD1Pendente   = false;
        private bool   _stopD2Pendente   = false;
        private bool   _stopD3Pendente   = false;
        private double _stopD1Preco      = 0;
        private double _stopD2Preco      = 0;
        private double _stopD3Preco      = 0;

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
                Name        = "stg20com34_v127c";
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
                UsarFiltroRSI5m     = true;   // GOLD: validado ON — filtra entradas de menor qualidade

                // FIX 2: tolerância RetornoV independente da banda de consolidação
                // Padrão 120t — mais amplo que RangeConsolidacaoTicks (80t)
                // Permite reconhecer RetornoV mesmo quando preço corrige mais fundo
                ToleranciRetornoVTicks = 40;   // v1.24: 120→40 — replica the best (v1.11 hardcoded RangeConsolidacaoTicks)

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
                UsarScoreExaustao   = true;   // GOLD: validado ON — Score=2, saídas antecipadas por exaustão
                NBarrasVolume       = 3;
                NBarrasDelta        = 2;
                NBarrasRSI          = 2;
                ScoreExaustaoMinimo = 2.0;    // GOLD: validado Score=2

                // Stop máximo por entrada (0 = desabilitado)
                StopMaxTicks = 0;

                // Diagnóstico
                ModoDebug = false;

                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                BarsRequiredToTrade          = 40;  // v1.23: revertido para 40 — warmup indicadores 1m; SMA(5m) protegida pelo guard BarsArray[1].Count < 25
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
            if (BarsArray[1].Count < 25) return;  // v1.23: 200→25 — aguarda warmup mínimo SMA(5m); guard excessivo eliminava ~40% das entradas válidas

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
                    // v1.25: verificação ANTES do timeout (comportamento v1.11 the best)
                    // Guard barExpulsao > 0 evita falso trigger se ResetarFSM() zerou a var
                    VerificarRetornoV();
                    if (barExpulsao > 0 && CurrentBar - barExpulsao > 60)
                    {
                        if (ModoDebug) Print(Time[0] + " | Timeout Expulsao — resetando");
                        ResetarFSM();
                    }
                    break;

                case EstadoFSM.RetornoV:
                    // v1.25: verificação ANTES do timeout (comportamento v1.11 the best)
                    // Guard barRetornoV > 0 evita falso trigger se ResetarFSM() zerou a var
                    VerificarCruzamento1m();
                    if (barRetornoV > 0 && CurrentBar - barRetornoV > 80)
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

            // Reset trail manual (FIX L v1.27)
            _trailHighMax    = 0;
            _trailLowMin     = 0;

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
                // FIX K v1.26: stop imediato na barra de entrada (= v1.11 the best)
                // _fillCompleto reemitirá SetStopLoss(Price) pós-fill para ancoragem absoluta
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
                // FIX K v1.26: stop imediato na barra de entrada (= v1.11 the best)
                // _fillCompleto reemitirá SetStopLoss(Price) pós-fill para ancoragem absoluta
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

                    // Bug 1+3 fix: stop inicial emitido APÓS fill completo, com Price (não Ticks)
                    // Evita fragmentação de posição e stop prematuro sobre fill parcial
                    SetStopLoss(nomeEntrada, CalculationMode.Price, _stopInicial, false);

                    if (ModoDebug) Print(Time[0] + " | Fill completo | _qtdInicial=" + _qtdInicial
                        + " | stop inicial @ " + _stopInicial.ToString("F2"));
                }
                // ── FIX A v1.19: Restart Recovery ────────────────────────────
                // Detecta posição herdada após MaxRestarts: posQtd > 0 mas < Contratos
                // porque a estratégia reiniciou no meio de uma posição existente.
                // Em vez de aguardar fill eterno, adota a quantidade real e emite stop
                // baseado em AveragePrice para proteger imediatamente.
                else if (posQtd > 0)
                {
                    _qtdInicial      = posQtd;                  // adota quantidade real herdada
                    _fillCompleto    = true;
                    _restartRecovery = true;

                    // precoEntrada era 0 (ExecutarEntrada não foi chamada nesta instância)
                    precoEntrada = Position.AveragePrice;

                    // Recalcula stop baseado no preço médio real da posição
                    _stopInicial = isLong
                        ? Position.AveragePrice - StopTicks * TickSize
                        : Position.AveragePrice + StopTicks * TickSize;

                    SetStopLoss(nomeEntrada, CalculationMode.Price, _stopInicial, false);

                    Print(Time[0] + " | ★ RESTART RECOVERY | qtd herdada=" + _qtdInicial
                        + " | avgPrice=" + Position.AveragePrice.ToString("F2")
                        + " | stop recalibrado @ " + _stopInicial.ToString("F2"));
                }
                // ─────────────────────────────────────────────────────────────
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

            // ── FIX M: despachar stops pendentes (diferidos da barra anterior) ──
            if (_stopD1Pendente)
            {
                SetStopLoss(nomeEntrada, CalculationMode.Price, _stopD1Preco, false);
                _stopD1Pendente = false;
                if (ModoDebug) Print(Time[0] + " | StopD1 despachado @ " + _stopD1Preco.ToString("F2"));
            }
            if (_stopD2Pendente)
            {
                SetStopLoss(nomeEntrada, CalculationMode.Price, _stopD2Preco, false);
                _stopD2Pendente = false;
                if (ModoDebug) Print(Time[0] + " | StopD2 despachado @ " + _stopD2Preco.ToString("F2"));
            }
            if (_stopD3Pendente)
            {
                SetStopLoss(nomeEntrada, CalculationMode.Price, _stopD3Preco, false);
                _stopD3Pendente = false;
                if (ModoDebug) Print(Time[0] + " | StopD3 despachado @ " + _stopD3Preco.ToString("F2"));
            }
            // ─────────────────────────────────────────────────────────────────

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

                // FIX M: NÃO emitir SetStopLoss aqui — fill ainda não confirmado.
                // Diferir para próxima barra via flag.
                double stopD1 = isLong
                    ? Math.Max(precoEntrada, _stopInicial)
                    : Math.Min(precoEntrada, _stopInicial);

                _stopD1Preco    = stopD1;
                _stopD1Pendente = true;

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

                // FIX M: diferir SetStopLoss
                _stopD2Preco    = novoStopD2;
                _stopD2Pendente = true;

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

                // FIX M: diferir SetStopLoss
                _stopD3Preco    = novoStopD3;
                _stopD3Pendente = true;

                d3Executada   = true;
                trailAtivo    = true;
                // FIX L v1.27c: ancora trail no precoD3 — evita inicialização
                // com High[0] da barra seguinte que pode já estar abaixo do D3
                _trailHighMax = precoD3;  // Long: pico inicial = preço de saída D3
                _trailLowMin  = precoD3;  // Short: vale inicial = preço de saída D3

                if (ModoDebug) Print(Time[0]
                    + " | D3 executada | qtd:" + qtdD3
                    + " | pnl:" + pnlTicks.ToString("F0") + "t"
                    + " | score:" + score.ToString("F2")
                    + " | stop → " + novoStopD3.ToString("F2")
                    + " | trail ativado");
                return;
            }

            // ── Trail após D3 — FIX L v1.27 ──────────────────────────────────
            // SetTrailStop substituído por gestão manual de stop em preço.
            // Motivo: SetTrailStop referencia a ordem original (7ct) e emite
            // stop pelo tamanho original — não pelo residual real (1ct).
            // Aqui: rastreamos o pico/vale manualmente e emitimos SetStopLoss(Price).
            if (trailAtivo && d3Executada && Position.Quantity > 0)
            {
                if (isLong)
                {
                    // Avança pelo High da barra — replica comportamento intrabar do SetTrailStop nativo
                    // _trailHighMax pré-inicializado em precoD3 (FIX L v1.27c)
                    if (High[0] > _trailHighMax) _trailHighMax = High[0];

                    double stopTrail = _trailHighMax - TrailTicks * TickSize;
                    SetStopLoss(nomeEntrada, CalculationMode.Price, stopTrail, false);

                    if (ModoDebug) Print(Time[0]
                        + " | Trail Long | highMax:" + _trailHighMax.ToString("F2")
                        + " | stopTrail:" + stopTrail.ToString("F2")
                        + " | pos:" + Position.Quantity);
                }
                else
                {
                    // Avança pelo Low da barra — replica comportamento intrabar do SetTrailStop nativo
                    // _trailLowMin pré-inicializado em precoD3 (FIX L v1.27c)
                    if (Low[0] < _trailLowMin) _trailLowMin = Low[0];

                    double stopTrail = _trailLowMin + TrailTicks * TickSize;
                    SetStopLoss(nomeEntrada, CalculationMode.Price, stopTrail, false);

                    if (ModoDebug) Print(Time[0]
                        + " | Trail Short | lowMin:" + _trailLowMin.ToString("F2")
                        + " | stopTrail:" + stopTrail.ToString("F2")
                        + " | pos:" + Position.Quantity);
                }
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
            // FIX M: limpar stops pendentes ao resetar FSM
            _stopD1Pendente  = false;
            _stopD2Pendente  = false;
            _stopD3Pendente  = false;
            _stopD1Preco     = 0;
            _stopD2Preco     = 0;
            _stopD3Preco     = 0;
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

                // ── FIX B v1.19: Fechamento externo não detectado ─────────────
                // Detecta quando a posição zerou por ordem externa (order-quick,
                // Zerar manual, etc.) que a estratégia não emitiu.
                // Condição: _fillCompleto=true (havia posição gerenciada) mas não
                // foi via StopCancelClose/Zerar (tratados no bloco abaixo) e não
                // foi via D1/D2/D3 completo (trail) — ou seja, ainda havia saldo.
                // Reset limpo de todos os flags para evitar reentrada indesejada.
                if (_fillCompleto)
                {
                    string nomeExec = execution.Order != null ? execution.Order.Name : "";
                    bool   foiStopEstrategia = (nomeExec == "StopCancelClose" || nomeExec == "Zerar");

                    if (!foiStopEstrategia)
                    {
                        if (ModoDebug) Print(time + " | Fechamento externo detectado (" + nomeExec
                            + ") → reset flags gestao");
                        _fillCompleto    = false;
                        _qtdInicial      = 0;
                        _restartRecovery = false;
                        d1Executada      = false;
                        d2Executada      = false;
                        d3Executada      = false;
                        trailAtivo       = false;
                        _trailHighMax    = 0;
                        _trailLowMin     = 0;
                        ResetarFSM();
                    }
                }
                // ─────────────────────────────────────────────────────────────
            }

            // C3: detectar fechamento de resíduo por stop invertido → limpar FSM
            if (execution.Order != null &&
                (execution.Order.Name == "StopCancelClose" ||
                 execution.Order.Name == "Zerar"))
            {
                if (ModoDebug) Print(time + " | OnExec: " + execution.Order.Name + " → ResetarFSM forçado");
                ResetarFSM();
                _fillCompleto    = false;
                _qtdInicial      = 0;
                _restartRecovery = false;
                d1Executada      = false;
                d2Executada      = false;
                d3Executada      = false;
                trailAtivo       = false;
                _trailHighMax    = 0;
                _trailLowMin     = 0;
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