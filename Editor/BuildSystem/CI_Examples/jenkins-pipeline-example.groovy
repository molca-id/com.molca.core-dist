// Jenkins Pipeline Example for Molca Build System
// Create a new Pipeline job in Jenkins and paste this script

pipeline {
    agent any
    
    parameters {
        choice(
            name: 'BUILD_PROFILE',
            choices: ['development', 'staging', 'production'],
            description: 'Build profile to use'
        )
        choice(
            name: 'BUILD_TARGET',
            choices: ['Win64', 'Android', 'iOS'],
            description: 'Platform to build for'
        )
    }
    
    environment {
        // Update these paths for your environment
        UNITY_PATH = "C:\\Program Files\\Unity\\Hub\\Editor\\2022.3.x\\Editor\\Unity.exe"
        PROJECT_PATH = "${WORKSPACE}"
        BUILD_LOG = "${WORKSPACE}\\build.log"
    }
    
    stages {
        stage('Checkout') {
            steps {
                checkout scm
            }
        }
        
        stage('Build') {
            steps {
                script {
                    def buildMethod = ""
                    switch(params.BUILD_PROFILE) {
                        case 'development':
                            buildMethod = "Molca.Editor.CommandLineBuild.BuildDevelopment"
                            break
                        case 'staging':
                            buildMethod = "Molca.Editor.CommandLineBuild.BuildStaging"
                            break
                        case 'production':
                            buildMethod = "Molca.Editor.CommandLineBuild.BuildProduction"
                            break
                    }
                    
                    // Build the project
                    bat """
                        "${UNITY_PATH}" ^
                        -quit ^
                        -batchmode ^
                        -nographics ^
                        -projectPath "${PROJECT_PATH}" ^
                        -buildTarget ${params.BUILD_TARGET} ^
                        -executeMethod ${buildMethod} ^
                        -logFile "${BUILD_LOG}"
                    """
                }
            }
            
            post {
                always {
                    // Archive build log
                    archiveArtifacts artifacts: 'build.log', allowEmptyArchive: true
                }
                success {
                    echo 'Build completed successfully!'
                    // Archive build output
                    archiveArtifacts artifacts: 'Builds/**/*', allowEmptyArchive: false
                }
                failure {
                    echo 'Build failed! Check build.log for details.'
                }
            }
        }
        
        stage('Deploy') {
            when {
                expression { params.BUILD_PROFILE == 'production' }
            }
            steps {
                echo 'Deploying production build...'
                // Add your deployment steps here
                // Examples:
                // - Upload to Steam
                // - Upload to itch.io
                // - Upload to internal server
                // - Trigger cloud deployment
            }
        }
    }
    
    post {
        success {
            // Send success notification
            echo "Build ${params.BUILD_PROFILE} for ${params.BUILD_TARGET} completed successfully!"
            // You can add Discord/Slack webhook notification here
        }
        failure {
            // Send failure notification
            echo "Build ${params.BUILD_PROFILE} for ${params.BUILD_TARGET} failed!"
        }
        cleanup {
            // Clean up workspace if needed
            echo 'Cleaning up...'
        }
    }
}

/*
Setup Instructions:

1. Install Jenkins and required plugins:
   - Git plugin
   - Pipeline plugin
   - Workspace Cleanup plugin (optional)

2. Create a new Pipeline job:
   - New Item > Pipeline
   - Name it "Unity Build"

3. Configure the Pipeline:
   - Pipeline definition: Pipeline script
   - Paste this script

4. Update environment variables:
   - UNITY_PATH: Path to Unity executable
   - Adjust paths for your system

5. Optional: Set up Unity license activation
   - May need to activate Unity in headless mode first

6. Run the build:
   - Click "Build with Parameters"
   - Select profile and target
   - Click "Build"

7. View results:
   - Check Console Output for logs
   - Download artifacts from build page
*/

