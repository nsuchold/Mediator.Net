module.exports = {
  publicPath: '/ViewBundle_Generic/',
  outputDir: '../../Run/DashboardDist/ViewBundle_Generic',
  configureWebpack: {
    performance: { hints: false }
  },
  pages: {
    variables: 'src/view_variables/main.ts',
    eventlog:  'src/view_eventlog/main.ts',
    history:   'src/view_history/main.ts',
    generic:   'src/view_generic/main.ts',
  }
}